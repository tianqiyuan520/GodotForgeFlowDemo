using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    // ── 建造命令的执行 ──────────────────────────────────────────────────────

    /// <summary>
    /// 撤销栈里的一条**动作**。撤销 = 把它反过来做，重做 = 再做一遍。
    ///
    /// ⚠ <c>Added</c> 记录**这条动作当初是「加」还是「删」**。少了它，撤销一次**拆除**
    ///   也会被当成「再拆一次」⇒ 撤销拆除什么都不做、而重做反而把它加回来。
    ///   这个坑是「用真实输入管线跑 拆掉 → Ctrl+Z → Ctrl+Y」时抓出来的（已加进探针 06）。
    ///
    /// 存的是**对象引用**，所以「拆掉再撤销」恢复出来的就是原来那一台，
    /// 连稳定 id 之外的一切（美术、配方、朝向）都一模一样。
    /// </summary>
    private abstract record UndoEntry(MachineDef? M, BeltDef? B, FluidPipeDef? P, bool Added);

    private sealed record MachineEntry(MachineDef Def, bool Added) : UndoEntry(Def, null, null, Added);

    /// <summary>一条带被拆时，**指向它的那一组机器绑定**（撤销时要接回去，见 <see cref="BeltEntry"/>）。</summary>
    private readonly record struct BindRef(MachineDef Def, int[] In, int[] Out);

    /// <summary>
    /// 一条带的动作。
    ///
    /// ⚠ <paramref name="OldId"/>/<paramref name="Bound"/> 只对「拆除」有意义：带被拆时
    ///   <see cref="FactoryLayout.RemoveBelt"/> 会把指向它的机器绑定清掉（否则槽位复用会让
    ///   新带"继承"旧绑定）。撤销时带会落到**新的**槽位 id 上，所以绑定的那一对 id
    ///   必须一起记下来并改指到新 id —— 不记的话「拆除 → Ctrl+Z」会**静默**少掉绑定：
    ///   带回来了、机器却不再接它（画面上看起来是"接上了但不来货"）。
    /// </summary>
    private sealed record BeltEntry(BeltDef Def, bool Added, int OldId = -1, BindRef[]? Bound = null)
        : UndoEntry(null, Def, null, Added);

    private sealed record PipeEntry(FluidPipeDef Def, bool Added) : UndoEntry(null, null, Def, Added);

    /// <summary>
    /// **轨道绑定**这条动作（机器 ↔ 带的一对角色）。
    ///
    /// ⚠ 它和其它条目不同：绑定不改「世界上有没有这个物件」，只改一台机器的**连线意图**。
    ///   所以两个方向不是"加/删同一个东西"，而是**两组 id**：
    ///   撤销 → 写回 <c>Old*</c>，重做 → 写回 <c>New*</c>（<c>Added</c> 恒为 true，
    ///   语义上"绑定"就是"加了一条连线"）。
    /// </summary>
    private sealed record BindEntry(MachineDef Def, int[] OldIn, int[] OldOut, int[] NewIn, int[] NewOut)
        : UndoEntry(Def, null, null, true);

    /// <summary>
    /// **一组动作**（目前只有「框选拆除」用）。撤销时整组一起反向，一次 Ctrl+Z 恢复整片 ——
    /// 否则拆掉 20 个东西要按 20 次 Ctrl+Z，玩家一定会觉得是 bug。
    ///
    /// ⚠ 恢复（add）时按**逆序**重放：删除是正序做的，逆序恢复与「逐个撤销」完全等价，
    ///   也让重放日志读起来就是倒着回放一遍。
    /// </summary>
    private sealed record AreaEntry(IReadOnlyList<UndoEntry> Items, bool Added)
        : UndoEntry(null, null, null, Added);

    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();

    /// <summary>撤销栈上限 —— 是一份内存保护，不是玩法限制。</summary>
    private const int MaxUndoDepth = 200;

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>剪贴板（蓝图）。复制与粘贴都在模拟线程做，所以这里不需要锁。</summary>
    private readonly List<BlueprintMachine> _clipMachines = new();
    private readonly List<GridPos[]> _clipBelts = new();

    /// <summary>与 <see cref="_clipBelts"/> **平行**的层号表（蓝图要连"高低轨"一起复制）。</summary>
    private readonly List<int> _clipBeltLevels = new();

    private readonly List<GridPos[]> _clipPipes = new();

    public int ClipboardMachines => _clipMachines.Count;
    public int ClipboardBelts => _clipBelts.Count;
    public int ClipboardPipes => _clipPipes.Count;
    public GridPos ClipboardOrigin { get; private set; }

    /// <summary>剪贴板里第 <paramref name="i"/> 条带的层号（预览层按它决定画在物品上面还是下面）。</summary>
    public int ClipboardBeltLevel(int i)
        => (uint)i < (uint)_clipBeltLevels.Count ? _clipBeltLevels[i] : 0;

    /// <summary>把队列里的命令按序全部应用，最后一次同步拓扑。**只在模拟线程调用。**</summary>
    private void DrainBuildQueue()
    {
        bool changed = false;
        while (_buildQueue.TryDequeue(out BuildCommand? cmd))
        {
            BuildResult result = ApplyBuild(cmd);
            LastBuildResult = result;
            if (result.Ok)
            {
                if (cmd is not CopyAreaCommand) changed = true;   // 复制不动布局
                AppliedBuildCount++;
            }
            else RejectedBuildCount++;
        }

        if (!changed) return;

        SyncTopology(_buildPowerPercent);
        _occupancy.Rebuild(_layout);
    }

    /// <summary>应用一条命令。返回结果；<c>Ok</c> 表示确实改动了布局（复制除外）。</summary>
    private BuildResult ApplyBuild(BuildCommand cmd)
    {
        switch (cmd)
        {
            case BuildMachineCommand bm:
                return PlaceMachine(bm);

            case BuildBeltCommand bb:
                return DrawBelt(bb);

            case BuildPipeCommand bp:
                return DrawPipe(bp);

            case BindMachineTrackCommand bt:
                return BindMachineTrack(bt);

            case RemoveAtCommand ra:
                return RemoveAt(ra.Pos);

            case RemoveAreaCommand rr:
                return RemoveArea(rr.A, rr.B);

            case UndoCommand:
                return UndoOrRedo(redo: false);

            case RedoCommand:
                return UndoOrRedo(redo: true);

            case CopyAreaCommand ca:
                return CopyArea(ca.A, ca.B);

            case PasteCommand pa:
                return Paste(pa.Origin, pa.WithBelts);

            default:
                return new BuildResult(BuildOutcome.BadShape, default, $"未知命令 {cmd.GetType().Name}");
        }
    }

    private void PushUndo(UndoEntry e)
    {
        _undo.Add(e);
        if (_undo.Count > MaxUndoDepth) _undo.RemoveAt(0);
        _redo.Clear();     // 新的动作让「重做」失效 —— 与所有编辑器一致
    }

    private int FindMachine(MachineDef def)
    {
        for (int m = 0; m < _layout.MachineSlotCount; m++)
            if (ReferenceEquals(_layout.MachineAt(m), def)) return m;
        return -1;
    }

    private int FindBelt(BeltDef def)
    {
        for (int b = 0; b < _layout.BeltSlotCount; b++)
            if (ReferenceEquals(_layout.BeltAt(b), def)) return b;
        return -1;
    }

    private int FindPipe(FluidPipeDef def)
    {
        for (int i = 0; i < _layout.PipeSlotCount; i++)
            if (ReferenceEquals(_layout.PipeAt(i), def)) return i;
        return -1;
    }

    /// <summary>撤销 / 重做。返回值只用于 HUD 与探针反馈。</summary>
    private BuildResult UndoOrRedo(bool redo)
    {
        List<UndoEntry> from = redo ? _redo : _undo;
        List<UndoEntry> to = redo ? _undo : _redo;

        if (from.Count == 0)
            return new BuildResult(BuildOutcome.NothingToDo, default,
                                   redo ? "没有可重做的动作" : "没有可撤销的动作");

        UndoEntry e = from[^1];
        from.RemoveAt(from.Count - 1);

        // 这条动作当初是「加」还是「删」，决定两个方向各自要做什么：
        //   撤销 ⇒ 反过来做（!Added）；重做 ⇒ 再做一遍（Added）。
        // 搬进另一条栈的是**这条动作本身**（不是它的反面）——
        // 那条栈的语义是「撤销/重做来回走时要重放的动作」。
        bool applyAdd = redo ? e.Added : !e.Added;
        string? detail = ApplyUndoEntry(e, applyAdd);
        if (detail == null)
            return new BuildResult(BuildOutcome.NothingThere, default,
                                   redo ? "重做失败：目标位置可能已被占用" : "撤销失败：目标位置可能已被占用");

        to.Add(e);
        TopologyRevision++;    // 撤销也是结构变更；让 HUD/探针能看到它发生了
        return new BuildResult(BuildOutcome.Placed, default, detail);
    }

    private string? ApplyUndoEntry(UndoEntry e, bool add)
    {
        switch (e)
        {
            case MachineEntry me:
                if (add)
                {
                    if (!_occupancy.CanPlaceMachine(me.Def.Pos, me.Def.SizeX, me.Def.SizeY)) return null;
                    int id = _layout.AddMachine(me.Def);
                    Occupy(me.Def, id);
                    return $"恢复机器 #{id}";
                }
                else
                {
                    int id = FindMachine(me.Def);
                    if (id < 0) return null;
                    _layout.RemoveMachine(id);
                    Free(me.Def);
                    return $"撤销机器 #{id}";
                }

            case BeltEntry be:
                if (add)
                {
                    // ⚠ 校验要用**这条带自己的层**：交错时另一层可能已经占着同一格，
                    //   用默认层去判会把「恢复高轨」误判成冲突（那格上本来就有低轨）。
                    foreach (GridPos p in be.Def.Cells)
                        if (!_occupancy.CanPlaceBelt(p, be.Def.Level)) return null;
                    int id = _layout.AddBelt(be.Def);
                    Occupy(be.Def, id);
                    // 把拆带时记下的绑定接回去，并改指到**新**的槽位 id（见 BeltEntry 的说明）
                    if (be.Bound != null)
                        foreach (BindRef r in be.Bound)
                        {
                            r.Def.InBeltBindings = Retarget(r.In, be.OldId, id);
                            r.Def.OutBeltBindings = Retarget(r.Out, be.OldId, id);
                        }
                    return be.Bound is { Length: > 0 }
                        ? $"恢复带 #{id}（并接回 {be.Bound.Length} 处绑定）"
                        : $"恢复带 #{id}";
                }
                else
                {
                    int id = FindBelt(be.Def);
                    if (id < 0) return null;
                    _layout.RemoveBelt(id);
                    Free(be.Def);
                    return $"撤销带 #{id}";
                }

            case BindEntry bd:
                if (FindMachine(bd.Def) < 0) return null;
                bd.Def.InBeltBindings = add ? bd.NewIn : bd.OldIn;
                bd.Def.OutBeltBindings = add ? bd.NewOut : bd.OldOut;
                return add
                    ? $"绑定 输入 {Describe(bd.NewIn)} / 输出 {Describe(bd.NewOut)}"
                    : $"恢复绑定 输入 {Describe(bd.OldIn)} / 输出 {Describe(bd.OldOut)}";

            case PipeEntry pe:
                if (add)
                {
                    foreach (GridPos p in pe.Def.Cells) if (!_occupancy.CanPlacePipe(p)) return null;
                    int id = _layout.AddPipe(pe.Def);
                    Occupy(pe.Def, id);
                    return $"恢复管道 #{id}";
                }
                else
                {
                    int id = FindPipe(pe.Def);
                    if (id < 0) return null;
                    _layout.RemovePipe(id);
                    Free(pe.Def);
                    return $"撤销管道 #{id}";
                }

            case AreaEntry ae:
            {
                // 组：整片一起反向。恢复用逆序（与逐个撤销等价）。
                int done = 0;
                if (add)
                {
                    for (int i = ae.Items.Count - 1; i >= 0; i--)
                        if (ApplyUndoEntry(ae.Items[i], true) != null) done++;
                }
                else
                {
                    foreach (UndoEntry item in ae.Items)
                        if (ApplyUndoEntry(item, false) != null) done++;
                }
                return done > 0 ? $"{done} 项（区域）" : null;
            }

            default:
                return null;
        }
    }

    private BuildResult PlaceMachine(BuildMachineCommand bm)
    {
        BuildableDef b = bm.Buildable;
        if (!_occupancy.CanPlaceMachine(bm.Pos, b.SizeX, b.SizeY))
        {
            bool inBounds = bm.Pos.X >= 0 && bm.Pos.Y >= 0
                            && bm.Pos.X + b.SizeX <= _occupancy.Width
                            && bm.Pos.Y + b.SizeY <= _occupancy.Height;
            return inBounds
                ? new BuildResult(BuildOutcome.Occupied, bm.Pos, "这一格已经有东西了")
                : new BuildResult(BuildOutcome.OutOfBounds, bm.Pos, "超出可建造范围");
        }

        // -1 = 新拉一条电网：取当前电网数作为新编号
        int net = bm.NetworkId >= 0 ? bm.NetworkId : _layout.NetworkCount;

        // 无配方（透传）的机器保留「按已建台数轮换初始物品」的老行为，
        // 配方机的产出由配方决定，不需要这个。
        ushort initial = b.RecipeIndex >= 0
            ? b.InitialItemType
            : (ushort)(1 + _layout.MachineCount % 8);

        MachineDef def = b.ToMachineDef(_content, bm.Pos, NormalizeFacing(bm.Facing), net, initial);
        int id = _layout.AddMachine(def);
        Occupy(def, id);
        PushUndo(new MachineEntry(def, Added: true));

        return new BuildResult(BuildOutcome.Placed, bm.Pos,
            $"机器 #{id}（{b.Name}，{b.SizeX}×{b.SizeY}）");
    }

    // ── 占用位图的**批内**同步 ──────────────────────────────────────────────
    //
    // ⚠ 占用位图**不能**等**整批**命令应用完之后才整体重建一次：那样同一批里
    //   「先拆、后放」会失败 —— 拆掉的那一刻布局已经变了，但位图还指着旧机器，
    //   于是紧接着的放置被判成「这一格已经有东西了」。
    //   玩家侧的症状是「先拆再放，放了没反应」—— 拖拽擦除后立刻铺带尤其容易撞上。
    //   （探针 10 的两段式仓储箱布局就是被这个卡住的：换上游矿机没换上。）
    //   ⇒ **每条命令应用后就地更新它碰过的那几格**；
    //   批量末尾的整体重建仍然保留，作为一致性的兜底。

    private void Occupy(MachineDef def, int id) => _occupancy.SetMachine(def.Pos, def.SizeX, def.SizeY, id);

    private void Occupy(BeltDef def, int id)
    {
        // ⚠ **地下段不占位** —— 这正是「轨道交错」能成立的原因：
        //   另一条带可以横穿这几格（占用校验只服务交互建造，但模拟侧的对齐也靠它，
        //   所以两处必须同口径：见 OccupancyGrid.Rebuild 里对同一批格子的跳过）。
        // ⚠ 占位必须**按层**写：交错的两条带共用一格，写成同一层的话后铺的那条
        //   会把先铺的抹掉（选/拆就会指到错的带上，见 OccupancyGrid.BeltAt）。
        for (int k = 0; k < def.Cells.Length; k++)
        {
            if (def.IsTunnelCell(k)) continue;
            _occupancy.SetBelt(def.Cells[k], id, def.Level);
        }
    }

    private void Occupy(FluidPipeDef def, int id)
    {
        foreach (GridPos p in def.Cells) _occupancy.SetPipe(p, id);
    }

    private void Free(MachineDef def)
    {
        foreach (GridPos p in def.Footprint()) _occupancy.Clear(p);
    }

    private void Free(BeltDef def)
    {
        // 只清**这一层**：交错时另一层的带还在那一格上，抹掉它就会「拆一条带带走上层/下层」。
        foreach (GridPos p in def.Cells) _occupancy.ClearBelt(p, def.Level);
    }

    private void Free(FluidPipeDef def)
    {
        foreach (GridPos p in def.Cells) _occupancy.Clear(p);
    }

    /// <summary>朝向夹到 0..3。</summary>
    private static int NormalizeFacing(int facing) => ((facing % 4) + 4) % 4;

    private BuildResult DrawBelt(BuildBeltCommand bb)
    {
        GridPos a = bb.From, b = bb.To;
        bool sameRow = a.Y == b.Y;
        bool sameCol = a.X == b.X;
        if (!sameRow && !sameCol)
            return new BuildResult(BuildOutcome.BadShape, a, "只能横着或竖着铺（斜线不支持）");

        int len = sameRow ? Math.Abs(b.X - a.X) + 1 : Math.Abs(b.Y - a.Y) + 1;
        if (len > _maxInstances / 2)
            return new BuildResult(BuildOutcome.TooLong, a,
                $"带太长（{len} 格，上限 {_maxInstances / 2}）—— 提高 maxInstances 即可");

        int wantLevel = FactoryLayout.NormalizeLevel(bb.Level);
        int stepX = sameRow ? Math.Sign(b.X - a.X) : 0;
        int stepY = sameCol ? Math.Sign(b.Y - a.Y) : 0;

        var cells = new GridPos[len];
        for (int i = 0; i < len; i++) cells[i] = new GridPos(a.X + stepX * i, a.Y + stepY * i);

        // 挑层：首选层照旧优先；被**同层的带**挡住而另一层空着 ⇒ 自动改走另一层（交错）。
        // 机器/管道换层也救不了 ⇒ 直接拒。规则只有一份，在 OccupancyGrid.TryResolveBeltLevel
        // 里，UI 的预览调的是同一个函数（预览的淡红/淡紫与松手后的结果因此同源）。
        if (!_occupancy.TryResolveBeltLevel(cells, wantLevel, out int level, out GridPos bad, out string? why))
            return new BuildResult(BuildOutcome.Occupied, bad, why ?? "这一格不可铺");

        // 上下游**不在这里推断** —— 交给 FactoryLayout.Resolve() 按几何推导。
        // 这样「先铺带、后建机器」也能连上（探针 06 抓到的坑）。
        var belt = new BeltDef { Cells = cells, Level = level };
        int beltId = _layout.AddBelt(belt);
        Occupy(belt, beltId);
        PushUndo(new BeltEntry(belt, Added: true));

        string auto = level != wantLevel
            ? $"（自动走{(level == 0 ? "低" : "高")}轨：与已有轨道交错）"
            : "";
        return new BuildResult(BuildOutcome.Placed, a,
            $"{(level == 0 ? "低" : "高")}轨带 #{beltId}（{len} 格）{auto}");
    }

    /// <summary>
    /// 给机器**加/减**一条轨道绑定（输入 / 输出 / 解除）。见 <see cref="BindMachineTrackCommand"/>。
    ///
    /// 校验都在这里做（UI 侧只是提前给反馈）：
    ///   · 那一格上真的有机器；
    ///   · 要绑的带**存在**、且不是别的机器的同一角色（一条带的上游/下游各只能有一台机器）；
    ///   · 同一条带不能同时当**同一台机器**的输入和输出（那是自环，货会在机器里打转）。
    /// ⚠ 绑定是**集合**：分流器要绑两条输出、合流器要绑两条输入（见 MachineDef.InBeltBindings）。
    /// </summary>
    private BuildResult BindMachineTrack(BindMachineTrackCommand bc)
    {
        int m = MachineCovering(bc.MachineCell);
        if (m < 0)
            return new BuildResult(BuildOutcome.NothingThere, bc.MachineCell, "这一格上没有机器");

        MachineDef def = _layout.MachineAt(m)!;
        int[] oldIn = def.InBeltBindings, oldOut = def.OutBeltBindings;

        if (bc.Role == TrackRole.Clear)
        {
            int[] inB = bc.BeltId < 0 ? Array.Empty<int>() : FactoryLayout.Without(oldIn, bc.BeltId);
            int[] outB = bc.BeltId < 0 ? Array.Empty<int>() : FactoryLayout.Without(oldOut, bc.BeltId);
            if (inB.Length == oldIn.Length && outB.Length == oldOut.Length)
                return new BuildResult(BuildOutcome.NothingThere, bc.MachineCell, "本来就没绑这条轨道");

            def.InBeltBindings = inB;
            def.OutBeltBindings = outB;
            PushUndo(new BindEntry(def, oldIn, oldOut, inB, outB));
            return new BuildResult(BuildOutcome.Placed, bc.MachineCell,
                $"机器 #{m} 解除绑定：输入 {Describe(inB)} · 输出 {Describe(outB)}" + WireHint(def));
        }

        if (!_layout.IsBeltAlive(bc.BeltId))
            return new BuildResult(BuildOutcome.NothingThere, bc.MachineCell, "要绑的轨道已经不存在了");

        bool asInput = bc.Role == TrackRole.Input;
        int[] mine = asInput ? def.InBeltBindings : def.OutBeltBindings;
        if (Array.IndexOf(mine, bc.BeltId) >= 0)
            return new BuildResult(BuildOutcome.NothingThere, bc.MachineCell, "已经绑过了");

        int holder = BindingHolder(bc.BeltId, asInput, exclude: m);
        if (holder >= 0)
            return new BuildResult(BuildOutcome.Occupied, bc.MachineCell,
                $"这条轨道已经被机器 #{holder} 绑成{(asInput ? "输入" : "输出")}了");

        // 同一条带不能既是这台机器的输入又是它的输出（自环：货在机器里打转，玩家看不出为什么）
        int[] opposite = asInput ? def.OutBeltBindings : def.InBeltBindings;
        if (Array.IndexOf(opposite, bc.BeltId) >= 0)
            return new BuildResult(BuildOutcome.Occupied, bc.MachineCell,
                "这条轨道已经是这台机器的另一个角色了");

        if (asInput) def.InBeltBindings = Add(def.InBeltBindings, bc.BeltId);
        else def.OutBeltBindings = Add(def.OutBeltBindings, bc.BeltId);

        PushUndo(new BindEntry(def, oldIn, oldOut, def.InBeltBindings, def.OutBeltBindings));
        string role = asInput ? "输入轨" : "输出轨";
        BeltDef bd = _layout.BeltAt(bc.BeltId)!;
        return new BuildResult(BuildOutcome.Placed, bc.MachineCell,
            $"机器 #{m} 的{role} += 带 #{bc.BeltId}（{bd.Cells.Length} 格，" +
            $"{(FactoryLayout.NormalizeLevel(bd.Level) == 0 ? "低" : "高")}轨）" + WireHint(def));
    }

    /// <summary>把一个 id 并进绑定集合（升序去重 —— 顺序确定是确定性的前提）。</summary>
    private static int[] Add(int[] bound, int id)
    {
        var next = new List<int>(bound) { id };
        next.Sort();
        return next.ToArray();
    }

    /// <summary>绑定集合的展示（空 = 未绑定）。</summary>
    private static string Describe(int[] belts)
        => belts.Length == 0 ? "未绑定" : string.Join(",", Array.ConvertAll(belts, b => $"#{b}"));

    /// <summary>把绑定集合里的 <paramref name="from"/> 换成 <paramref name="to"/>（撤销恢复带时用）。</summary>
    private static int[] Retarget(int[] bound, int from, int to)
    {
        if (from < 0 || Array.IndexOf(bound, from) < 0) return bound;
        var next = new List<int>(bound.Length);
        foreach (int b in bound) next.Add(b == from ? to : b);
        next.Sort();
        return next.ToArray();
    }

    /// <summary>
    /// **把「按几何摆好、还没接线」的机器一次接上**（场景构造者 / 测试用）。
    /// 见 <see cref="FactoryLayout.AutoBindGeometrically"/> —— 游戏里的建造路径**不会**调它。
    /// </summary>
    public void AutoWireUnboundMachines()
    {
        _layout.AutoBindGeometrically();
        SyncTopology(_buildPowerPercent);
        _occupancy.Rebuild(_layout);
    }

    /// <summary>接线状态提示：还缺角色时明确说出来（机器必须接线才能用）。</summary>
    private static string WireHint(MachineDef def)
        => def.IsWired ? "" : "  ⚠ 还没接够，机器不会工作";

    /// <summary>某一格上那台机器的 id：先看这格自己的记录，再退化成"占地覆盖这一格"的机器。</summary>
    private int MachineCovering(GridPos p)
    {
        int m = _occupancy.MachineAt(p);
        if (m >= 0) return m;
        for (int i = 0; i < _layout.MachineSlotCount; i++)
        {
            MachineDef? def = _layout.MachineAt(i);
            if (def == null) continue;
            if (p.X >= def.Pos.X && p.X < def.Pos.X + def.SizeX &&
                p.Y >= def.Pos.Y && p.Y < def.Pos.Y + def.SizeY) return i;
        }
        return -1;
    }

    /// <summary>这条带被哪台机器绑成了该角色（-1 = 没人绑）。排除 <paramref name="exclude"/>。</summary>
    private int BindingHolder(int beltId, bool asInput, int exclude = -1)
    {
        for (int i = 0; i < _layout.MachineSlotCount; i++)
        {
            if (i == exclude) continue;
            MachineDef? def = _layout.MachineAt(i);
            if (def == null) continue;
            int[] bound = asInput ? def.InBeltBindings : def.OutBeltBindings;
            if (Array.IndexOf(bound, beltId) >= 0) return i;
        }
        return -1;
    }

    /// <summary>拆带前把「指向这条带的机器绑定」记下来（撤销时接回去，见 <see cref="BeltEntry"/>）。</summary>
    private BindRef[] CaptureBindings(int beltId)
    {
        var list = new List<BindRef>();
        for (int i = 0; i < _layout.MachineSlotCount; i++)
        {
            MachineDef? def = _layout.MachineAt(i);
            if (def == null) continue;
            if (def.IsBoundInput(beltId) || def.IsBoundOutput(beltId))
                list.Add(new BindRef(def, def.InBeltBindings, def.OutBeltBindings));
        }
        return list.ToArray();
    }

    private BuildResult DrawPipe(BuildPipeCommand bp)
    {
        GridPos a = bp.From, b = bp.To;
        bool sameRow = a.Y == b.Y, sameCol = a.X == b.X;
        if (!sameRow && !sameCol)
            return new BuildResult(BuildOutcome.BadShape, a, "管道也只能横或竖");

        int len = sameRow ? Math.Abs(b.X - a.X) + 1 : Math.Abs(b.Y - a.Y) + 1;
        int stepX = sameRow ? Math.Sign(b.X - a.X) : 0;
        int stepY = sameCol ? Math.Sign(b.Y - a.Y) : 0;

        var cells = new GridPos[len];
        for (int i = 0; i < len; i++)
        {
            var p = new GridPos(a.X + stepX * i, a.Y + stepY * i);
            if (!_occupancy.CanPlacePipe(p))
                return new BuildResult(BuildOutcome.Occupied, p, $"第 {i} 格已经有东西了");
            cells[i] = p;
        }

        var pipe = new FluidPipeDef { Cells = cells };
        int id = _layout.AddPipe(pipe);
        Occupy(pipe, id);
        PushUndo(new PipeEntry(pipe, Added: true));
        return new BuildResult(BuildOutcome.Placed, a, $"管道 #{id}（{len} 格）");
    }

    private BuildResult RemoveAt(GridPos p)
    {
        int machine = _occupancy.MachineAt(p);
        if (machine >= 0)
        {
            MachineDef def = _layout.MachineAt(machine)!;
            _layout.RemoveMachine(machine);
            Free(def);
            PushUndo(new MachineEntry(def, Added: false));
            return new BuildResult(BuildOutcome.Placed, p, $"拆掉机器 #{machine}");
        }

        int belt = _occupancy.BeltAt(p);
        if (belt >= 0)
        {
            BeltDef def = _layout.BeltAt(belt)!;
            // ⚠ 绑定要在 **RemoveBelt 之前**记下来：RemoveBelt 会把指向这条带的绑定清掉
            //   （槽位复用安全），而撤销必须能把它们接回来（见 BeltEntry）。
            BindRef[] bound = CaptureBindings(belt);
            _layout.RemoveBelt(belt);
            Free(def);
            PushUndo(new BeltEntry(def, Added: false, OldId: belt, Bound: bound));
            return new BuildResult(BuildOutcome.Placed, p,
                bound.Length > 0 ? $"拆掉带 #{belt}（连带解除 {bound.Length} 处绑定）" : $"拆掉带 #{belt}");
        }

        int pipe = _occupancy.PipeAt(p);
        if (pipe >= 0)
        {
            FluidPipeDef def = _layout.PipeAt(pipe)!;
            _layout.RemovePipe(pipe);
            Free(def);
            PushUndo(new PipeEntry(def, Added: false));
            return new BuildResult(BuildOutcome.Placed, p, $"拆掉管道 #{pipe}");
        }

        return new BuildResult(BuildOutcome.NothingThere, p, "这一格是空的");
    }

    /// <summary>
    /// 框选拆除：拆掉矩形区域（含端点）里的一切，并把它记成**一条撤销组**。
    ///
    /// 选取规则与 <see cref="CopyArea"/> 一致（机器看左上角、带/管道沾到就整段拆），
    /// 所以「复制什么」与「拆什么」是同一套语义 —— 玩家不用记两套边界规则。
    ///
    /// ⚠ 顺序：**先机器、再带、再管道**。带/管道会按 id 遍历，所以结果是确定的；
    ///   而机器先拆掉能立刻释放它占的格子，后面的判定读到的是最新状态（`Free` 是就地的）。
    /// </summary>
    private BuildResult RemoveArea(GridPos a, GridPos b)
    {
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);

        var items = new List<UndoEntry>();
        int machines = 0, belts = 0, pipes = 0;

        for (int m = 0; m < _layout.MachineSlotCount; m++)
        {
            MachineDef? def = _layout.MachineAt(m);
            if (def == null) continue;
            if (def.Pos.X < x0 || def.Pos.X > x1 || def.Pos.Y < y0 || def.Pos.Y > y1) continue;
            _layout.RemoveMachine(m);
            Free(def);
            items.Add(new MachineEntry(def, Added: false));
            machines++;
        }

        for (int bi = 0; bi < _layout.BeltSlotCount; bi++)
        {
            BeltDef? def = _layout.BeltAt(bi);
            if (def == null) continue;
            if (!TouchesRect(def.Cells, x0, x1, y0, y1)) continue;
            BindRef[] bound = CaptureBindings(bi);
            _layout.RemoveBelt(bi);
            Free(def);
            items.Add(new BeltEntry(def, Added: false, OldId: bi, Bound: bound));
            belts++;
        }

        for (int pi = 0; pi < _layout.PipeSlotCount; pi++)
        {
            FluidPipeDef? def = _layout.PipeAt(pi);
            if (def == null) continue;
            if (!TouchesRect(def.Cells, x0, x1, y0, y1)) continue;
            _layout.RemovePipe(pi);
            Free(def);
            items.Add(new PipeEntry(def, Added: false));
            pipes++;
        }

        if (items.Count == 0)
            return new BuildResult(BuildOutcome.NothingThere, new GridPos(x0, y0), "框里没有可拆的东西");

        PushUndo(new AreaEntry(items, Added: false));
        return new BuildResult(BuildOutcome.Placed, new GridPos(x0, y0),
            $"拆掉 {machines} 机器 / {belts} 带 / {pipes} 管道（{x1 - x0 + 1}×{y1 - y0 + 1}）");
    }

    private static bool TouchesRect(GridPos[] cells, int x0, int x1, int y0, int y1)
    {
        foreach (GridPos c in cells)
            if (c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1) return true;
        return false;
    }

}
