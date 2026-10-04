namespace ForgeFlow.Sim;

/// <summary>
/// 网格占用位图 —— 玩家建造的**校验层**。
///
/// 职责很单一：回答「这一格能不能放」。它**不参与模拟**（模拟只认布局的拓扑），
/// 所以它可以随时从布局重建，也不需要任何确定性保证 —— 这正是把它单独分出来的原因。
///
/// 为什么不用「遍历所有机器看有没有重叠」：
///   放置是高频交互（画笔拖过一格就要判一次），O(机器数) 的判定在 10,000 台机器时是
///   每次操作 10,000 次比较。位图是 O(1)，而且是玩家操作级别的东西，没有理由省这份内存。
///
/// 坐标约定：**格子坐标即数组下标**，原点 (0,0) 在左上，y 向下。
/// 负坐标与超出范围的坐标一律视为「越界 = 不可放」，不需要额外判断。
///
/// ══ 为什么要**按层**存带（而不是一格一个 int）══════════════════════════════
///
/// 带可以**交错**：同一格允许上下两层各有一条带（见 <see cref="CanPlaceBelt(GridPos, int)"/>）。
/// 一格一个 int 就表达不了「这格既有低轨又有高轨」—— 后铺的那条会被判成"这格已经有东西了"，
/// 于是交错永远铺不出来。所以带单独按层开两张表（<see cref="FactoryLayout.MaxLevels"/> 档），
/// 机器的表则保持一格一台（**轨道不许压在机器上**，这是玩法约束，不是实现限制）。
///
/// ⚠ 「这格上的带是谁」现在是**多值**的：需要"最上面那条"就用 <see cref="BeltAt(GridPos)"/>
///   （高轨优先），需要"某一层的"就用按层重载。选/拆一律走前者 —— 玩家看到的是最上面那条。
/// </summary>
public sealed class OccupancyGrid
{
    public int Width { get; }
    public int Height { get; }

    // 0 = 空；正数 = 机器 id + 1
    private readonly int[] _machines;
    // 管道单独一张表：它和机器/带一样占格，但语义不同（管网是连通分量，不是路径）
    private readonly int[] _pipes;
    // 逐层的带：0 = 空；正数 = 带 id + 1。下标 = 层号。
    private readonly int[][] _belts;

    public OccupancyGrid(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _machines = new int[width * height];
        _pipes = new int[width * height];
        _belts = new int[FactoryLayout.MaxLevels][];
        for (int lv = 0; lv < _belts.Length; lv++) _belts[lv] = new int[width * height];
    }

    private bool InBounds(GridPos p) => (uint)p.X < (uint)Width && (uint)p.Y < (uint)Height;
    private int Index(GridPos p) => p.Y * Width + p.X;

    /// <summary>格子上有没有东西（机器 / 带 / 管道任一，不含层）。</summary>
    public bool IsOccupied(GridPos p)
    {
        if (!InBounds(p)) return false;
        int i = Index(p);
        if (_machines[i] != 0 || _pipes[i] != 0) return true;
        foreach (int[] layer in _belts) if (layer[i] != 0) return true;
        return false;
    }

    /// <summary>格子能不能放东西（**任意一层都没东西**才算空）。越界也算不能。</summary>
    public bool IsFree(GridPos p) => InBounds(p) && !IsOccupied(p);

    /// <summary>格子上的管道 id；没有就是 -1。</summary>
    public int PipeAt(GridPos p)
    {
        if (!InBounds(p)) return -1;
        int v = _pipes[Index(p)];
        return v > 0 ? v - 1 : -1;
    }

    public void SetPipe(GridPos p, int pipeId)
    {
        if (!InBounds(p)) return;
        _pipes[Index(p)] = pipeId + 1;
    }

    /// <summary>能不能在这一格铺管道：格子必须空着（管道不分层，它的层没意义）。</summary>
    public bool CanPlacePipe(GridPos p) => IsFree(p);

    /// <summary>格子上的机器 id；没有就是 -1。</summary>
    public int MachineAt(GridPos p)
    {
        if (!InBounds(p)) return -1;
        int v = _machines[Index(p)];
        return v > 0 ? v - 1 : -1;
    }

    /// <summary>
    /// 格子上的带 id；没有就是 -1。**多层时返回最上面那条**（层号大的优先）——
    /// 玩家点一格想选/想拆的是他看到的那个（高轨压在低轨之上，见快照的绘制顺序）。
    /// </summary>
    public int BeltAt(GridPos p)
    {
        for (int lv = _belts.Length - 1; lv >= 0; lv--)
        {
            int v = BeltAt(p, lv);
            if (v >= 0) return v;
        }
        return -1;
    }

    /// <summary>某一**层**上的带 id；没有就是 -1。</summary>
    public int BeltAt(GridPos p, int level)
    {
        if (!InBounds(p)) return -1;
        int v = _belts[FactoryLayout.NormalizeLevel(level)][Index(p)];
        return v > 0 ? v - 1 : -1;
    }

    public void SetMachine(GridPos p, int machineId)
    {
        if (!InBounds(p)) return;
        _machines[Index(p)] = machineId + 1;
    }

    /// <summary>在某层写一条带（层号越界会被夹到合法范围）。</summary>
    public void SetBelt(GridPos p, int beltId, int level = 0)
    {
        if (!InBounds(p)) return;
        _belts[FactoryLayout.NormalizeLevel(level)][Index(p)] = beltId + 1;
    }

    /// <summary>清掉某一层的带（**不动其它层**，也不动机器/管道）——
    /// 交错的两条带共用一格，拆掉高轨不能顺手把低轨也抹掉。</summary>
    public void ClearBelt(GridPos p, int level = 0)
    {
        if (!InBounds(p)) return;
        _belts[FactoryLayout.NormalizeLevel(level)][Index(p)] = 0;
    }

    /// <summary>清掉机器与管道（**带由 <see cref="ClearBelt"/> 单独清**，因为要按层清）。</summary>
    public void Clear(GridPos p)
    {
        if (!InBounds(p)) return;
        int i = Index(p);
        _machines[i] = 0;
        _pipes[i] = 0;
    }

    /// <summary>
    /// 能不能在 <paramref name="p"/> 放一台 <paramref name="sizeX"/>×<paramref name="sizeY"/> 的机器：
    /// **整个占地范围都要空着且在界内**（任意一层有带都算被占 —— 机器不能压在轨道上）。
    /// </summary>
    public bool CanPlaceMachine(GridPos p, int sizeX = 1, int sizeY = 1)
    {
        int sx = sizeX < 1 ? 1 : sizeX;
        int sy = sizeY < 1 ? 1 : sizeY;
        for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
                if (!IsFree(new GridPos(p.X + x, p.Y + y))) return false;
        return true;
    }

    /// <summary>把一台机器的整个占地范围标记为占用。</summary>
    public void SetMachine(GridPos p, int sizeX, int sizeY, int machineId)
    {
        int sx = sizeX < 1 ? 1 : sizeX;
        int sy = sizeY < 1 ? 1 : sizeY;
        for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
                SetMachine(new GridPos(p.X + x, p.Y + y), machineId);
    }

    /// <summary>
    /// 能不能在 <paramref name="p"/> 的 <paramref name="level"/> 层铺带。判据三条：
    ///   ① 没有机器（**轨道不许压在机器上**）；
    ///   ② 没有管道（管道是同一层的东西，而且它不是一进一出的路径，混在一起没意义）；
    ///   ③ **这一层**还没有带。
    /// ⇒ 不同层可以叠在同一格上，这就是「交错」；同层不行（那是两条带抢一条路）。
    ///
    /// 注意这里**只做占用校验，不做连通性/相邻校验** —— 铺带允许连着已有带
    /// （那正是自动铺瓦要处理的），也允许暂时悬空（玩家边画边改）。
    /// 把「能不能放」和「放了合不合理」分开，交互层才好给出不同的反馈。
    /// </summary>
    public bool CanPlaceBelt(GridPos p, int level = 0) => BeltBlockReason(p, level) is null;

    /// <summary>
    /// 这一格**为什么**不能铺带（给玩家的拒绝原因；能铺返回 null）。
    /// 单独一个方法是为了让 UI 与模拟说同一句话：几何上懂，提示上也要懂。
    /// </summary>
    public string? BeltBlockReason(GridPos p, int level)
    {
        if (!InBounds(p)) return "越出可建范围";
        if (MachineAt(p) >= 0) return "压在机器上（轨道不能盖机器）";
        if (PipeAt(p) >= 0) return "压在管道上";
        if (BeltAt(p, level) >= 0) return $"这一层（{(FactoryLayout.NormalizeLevel(level) == 0 ? "低轨" : "高轨")}）已经有带";
        return null;
    }

    /// <summary>
    /// **给一整串格子挑一个能铺的层** —— 这就是「玩家铺带时允许交错」的落点。
    ///
    /// 规则（先看首选层，不行再让另一层兜底）：
    ///   ① 首选层整串都空 ⇒ 用它（玩家按 `T` 选的那一层永远优先，不会被悄悄改掉）；
    ///   ② 首选层被**同层的带**挡住、而另一层整串都空 ⇒ **自动改用另一层**
    ///      （玩家拖着穿过已有轨道就会自动变成"交错"，不需要先记住自己在哪一层）；
    ///   ③ 机器与管道**不分层**：压在它们上面在哪一层都不行 ⇒ 直接拒绝
    ///      （**不能在机器上建造**）。
    ///
    /// ⚠ 模拟（`FactorySim.DrawBelt`）与 UI 预览**必须调同一个函数**：各写一份的话
    ///   预览里的"淡红不可铺"和松手后的实际结果迟早会对不上（本项目已经因为
    ///   「预览与提交各写一份」坏过一次，见 BeltTileAt 的注释）。
    /// </summary>
    /// <param name="cells">整条带的格子（顺序无关，只看集合）。</param>
    /// <param name="preferred">玩家当前选的层。</param>
    /// <param name="level">最终用哪一层（能铺时才有意义）。</param>
    /// <param name="bad">铺不了时：第一个出问题的格子。</param>
    /// <param name="reason">铺不了时：给玩家看的原因（优先报机器/管道这种"哪层都不行"的原因）。</param>
    public bool TryResolveBeltLevel(IReadOnlyList<GridPos> cells, int preferred,
                                    out int level, out GridPos bad, out string? reason)
    {
        int want = FactoryLayout.NormalizeLevel(preferred);
        level = want;
        bad = cells.Count > 0 ? cells[0] : default;
        reason = null;
        if (cells.Count == 0) { reason = "空的带"; return false; }

        string? hardBlock = null;          // 机器/管道：换层也救不了
        string? softBlock = null;          // 同层已有带：换层可能救得回来
        // ⚠ 不能用 `GridPos == default`（即 (0,0)）当「还没记过」的哨兵 ——
        //   **(0,0) 是合法格子**：首选层第一个被挡住的格恰好是 (0,0) 时，
        //   它会被后一层的挡住格覆盖掉，高亮出来的就不是真正拦路的那一格。
        //   用独立的布尔量记「记过没有」。
        GridPos hardCell = default, softCell = default;
        bool haveHard = false, haveSoft = false;

        for (int lv = 0; lv < FactoryLayout.MaxLevels; lv++)
        {
            // 先看首选层，再看其余的（层数上限是 2，这里写成循环是为了将来加层时不用改结构）
            int cur = lv == 0 ? want : NextLevel(want, lv);
            if (cur < 0) continue;

            GridPos blockedAt = default;
            string? why = null;
            foreach (GridPos p in cells)
            {
                why = BeltBlockReason(p, cur);
                if (why == null) continue;
                blockedAt = p;
                break;
            }

            if (why == null) { level = cur; reason = null; return true; }

            bool isBeltOnly = BeltAt(blockedAt, cur) >= 0 && MachineAt(blockedAt) < 0 && PipeAt(blockedAt) < 0;
            if (isBeltOnly) { softBlock ??= why; if (!haveSoft) { softCell = blockedAt; haveSoft = true; } }
            else { hardBlock ??= why; if (!haveHard) { hardCell = blockedAt; haveHard = true; } }
        }

        // 全都铺不了：优先报「哪层都不行」的那个原因（机器/管道），否则报同层冲突
        if (hardBlock != null) { reason = hardBlock; bad = hardCell; }
        else { reason = softBlock; bad = softCell; }
        return false;

        static int NextLevel(int want, int step)
        {
            int lv = (want + step) % FactoryLayout.MaxLevels;
            return lv;
        }
    }

    /// <summary>从布局重建整张位图。布局变了就调一次 —— O(机器数 + 格子数)。</summary>
    public void Rebuild(FactoryLayout layout)
    {
        Array.Clear(_machines, 0, _machines.Length);
        Array.Clear(_pipes, 0, _pipes.Length);
        foreach (int[] layer in _belts) Array.Clear(layer, 0, layer.Length);

        for (int m = 0; m < layout.MachineSlotCount; m++)
        {
            MachineDef? def = layout.MachineAt(m);
            if (def != null) SetMachine(def.Pos, def.SizeX, def.SizeY, m);
        }
        for (int b = 0; b < layout.BeltSlotCount; b++)
        {
            BeltDef? def = layout.BeltAt(b);
            if (def == null) continue;
            // ⚠ **地下段不占位**：这正是「地下带可以横穿」能成立的原因，必须与
            //   FactorySim.Occupy(BeltDef,int) 同口径 —— 两边不一致的话，
            //   一次批量建造之后的重建会把刚刚让出来的格子又占回去（症状：穿不过去，
            //   而且只在「同一批里先铺后重建」时才出现）。
            for (int k = 0; k < def.Cells.Length; k++)
            {
                if (def.IsTunnelCell(k)) continue;
                SetBelt(def.Cells[k], b, def.Level);
            }
        }
        for (int i = 0; i < layout.PipeSlotCount; i++)
        {
            FluidPipeDef? def = layout.PipeAt(i);
            if (def == null) continue;
            foreach (GridPos p in def.Cells) SetPipe(p, i);
        }
    }
}
