using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    // ── 快照 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 沿带的路径把「距入口多少像素」换算成世界坐标，**支持拐角**。
    ///
    /// 为什么要按路径算而不是「起点 + 距离」：这套美术的 4 向连通瓦片已经就位，
    /// 带很快就会有拐角；那时「起点 + 距离」会把物品画到带子外面去。
    /// 现在是直线，公式退化成和原来一样，所以行为不变。
    /// </summary>
    private static void BeltPointAt(BeltDef def, float distPx, out float wx, out float wy)
    {
        GridPos[] cells = def.Cells;
        int n = cells.Length;
        if (n == 0) { wx = 0; wy = 0; return; }

        // 距入口 t 像素 ⇒ 相对格心的浮点索引（格心在 (k + 0.5) 处）。
        //
        // ⚠⚠ **这个 −1.0 是「物品占格心」的约定，不能写成 −0.5。**
        //
        //   物品的行程 d ∈ [0, n格]：d = 0 是入口边，d = n格 是出口边。
        //   而格心在路径位置 0.5、1.5、…、n−0.5。要让**物品落进格子**，
        //   映射必须是 `tc = d − 1`，于是：
        //     · 满带时第 i 件（acc = i 格）⇒ tc = n−1−i ⇒ **正好落在第 n−1−i 格的格心**；
        //     · 任意两件相邻物品间距 ≥ MinGapSub = 1 格 ⇒ tc 相差 ≥ 1.0
        //       ⇒ **渲染出来永远至少差 1 格，不可能叠**。
        //
        //   ⚠ **不要**写成 `d − 0.5`：满带第 0 件 tc = n−0.5 会被下面的钳位拽回最后一格心，
        //   而第 1 件落在 n−1.5 —— 两者只差 **0.5 格**，精灵宽 1 格 ⇒ **叠掉一半**。
        //   这个错误只在**满带 / 堵塞**时才现形（只有那时 head 的 gap 才是 0）。
        float tc = distPx / FactoryLayout.CellPx - 1f;
        if (tc <= 0f)
        {
            (wx, wy) = FactoryLayout.GridToWorld(cells[0]);
            return;
        }
        if (tc >= n - 1)
        {
            (wx, wy) = FactoryLayout.GridToWorld(cells[n - 1]);
            return;
        }

        int i = (int)tc;
        float f = tc - i;
        (float ax, float ay) = FactoryLayout.GridToWorld(cells[i]);
        (float bx, float by) = FactoryLayout.GridToWorld(cells[i + 1]);
        wx = ax + (bx - ax) * f;
        wy = ay + (by - ay) * f;
    }

    /// <summary>
    /// 这一格是不是**拐角**（两条腿方向不同）。
    ///
    /// ⚠ 拐角格**不能滚动**：拐角瓦片的图案是弧形、不是可重复条纹，滚它会让拐角与两侧的
    ///   直线段错位（症状：拐弯处「撕裂 / 错开半格」）。
    ///   判据与 <see cref="BeltTileAt"/> 里的 `straight` 完全一致，改一处必须改另一处。
    /// </summary>
    private static bool BeltCellIsCorner(GridPos[] cells, int k)
    {
        int mask = 0;
        if (k > 0) mask |= DirBit(cells[k - 1], cells[k]);
        if (k + 1 < cells.Length) mask |= DirBit(cells[k + 1], cells[k]);
        bool straight = IsStraightMask(mask);
        return !straight;
    }

    /// <summary>
    /// 某段带第 <paramref name="k"/> 格的**流向**：返回 (滚动轴, 是否正方向)。
    /// 轴 +1 = 沿 X（横向带），−1 = 沿 Y（纵向带）。
    ///
    /// 流向就是**物品的走向**：<c>cells[0] → cells[^1]</c>（见 <see cref="BeltPointAt"/>）。
    /// 所以每格取「本格→下一格」的连线；**末格**没有下一格，用「上一格→本格」的连线
    /// （两者方向必然一致，因为一段带是直线 —— <c>DrawBelt</c> 只造横/竖两种形状）。
    ///
    /// ⚠ 轴和方向都必须传出去：**不要**固定「横向、向右滚」—— 纵向带会被横向平移，
    ///   反向的带也会看着往反方向流。
    /// </summary>
    private static (float Axis, bool Forward) BeltFlow(BeltDef def, int k)
    {
        GridPos[] cells = def.Cells;
        if (cells.Length < 2) return (1f, true);          // 单格带没有方向，当横向

        int a = k + 1 < cells.Length ? k : cells.Length - 2;
        GridPos u = cells[a], v = cells[a + 1];
        return u.X != v.X
            ? (1f, v.X > u.X)                              // 横向：往东为正
            : (-1f, v.Y > u.Y);                            // 纵向：往南为正
    }

    private void WriteSnapshot()
    {
        // 诊断开关：只测模拟侧时跳过快照（两笔预算分开测，见 SnapshotEnabled）。
        // ⚠ 跳过后 ItemsOnBelts 会保留上一次的值 —— 它只在快照里统计，这是有意的：
        //   它是个只增/只反映“上一次写出”的观测量，不参与任何后续决策。
        if (!SnapshotEnabled) return;

        long t0 = Stopwatch.GetTimestamp();
        try
        {
            WriteSnapshotCore();
        }
        finally
        {
            // 放在 finally：即使中途抛异常，指标也不会停在「上一次的值」上骗人。
            LastSnapshotMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private void WriteSnapshotCore()
    {
        RenderSnapshot s = Buffers.BeginWrite();
        _snapshotWriter ??= new SnapshotWriter(this);
        SnapshotWriter w = _snapshotWriter;
        w.Begin(s);

        // ══ **按层画出顺序**（MultiMesh 没有逐实例 Z 排序，实例顺序就是遮挡顺序）════════
        //
        // 用户要求：「低轨道的物品应当被高轨道挡住」。所以顺序必须是
        //     低轨带面 → 管道 → 机器 → 低轨物品 → 高轨带面 → 高轨物品
        // 而不是「所有带面 → 管道 → 机器 → 所有物品」—— 后者会让低轨的物品跑到高轨
        // 的瓦片**上面**去，低轨反而"穿"过了高轨。
        //
        // 为什么管道/机器夹在低轨带面与低轨物品之间：这是**引入分层之前就有的相对顺序**
        // （物品压在机器上、管道在带面之上），原样保留 ⇒ 单层场景的画面逐像素不变。
        for (int level = 0; level < FactoryLayout.MaxLevels; level++)
        {
            w.BeltBases(level);
            if (level == 0)
            {
                w.Pipes();
                w.Machines();
            }
            w.Items(level);
        }

        ItemsOnBelts = w.ItemCount;
        LastCulledInstances = w.Culled;
        s.LiveCount = w.Live;
        s.Tick = StepCount;
        Buffers.EndWrite();
        LiveCount = w.Live;
    }

    /// <summary>复用同一份写快照状态（稳态零分配），见 <see cref="WriteSnapshotCore"/>。</summary>
    private SnapshotWriter? _snapshotWriter;

    /// <summary>
    /// 把一帧快照写进实例缓冲的**分步实现**。
    ///
    /// 为什么抽成一个类而不是继续写在一个大方法里：分层要求把「带面」与「物品」两趟
    /// **按层各走一遍**，而这两趟的循环体非常长（各自带一大堆注释与剔除判断）。
    /// 复制两份是最坏的做法 —— 那种重复迟早会让两层的行为走岔（本项目已经因为
    /// 「两处各写一份」坏过：预览与提交、两张 LUT 表）。
    /// 抽成方法后「同一份代码只跑两次」，层号是参数。
    ///
    /// 它持有外层的引用是为了读带/机器/物品的 SoA 状态；**这些字段都只在模拟线程上被读写**
    /// （快照本来就在模拟线程填充），所以不需要任何同步。
    /// </summary>
    private sealed class SnapshotWriter
    {
        private readonly FactorySim _f;
        private float[] _xy = null!;
        private ushort[] _sprite = null!;
        private uint[] _tint = null!;
        private float[] _scale = null!;
        private float[] _scroll = null!;
        private float[] _scrollAxis = null!;
        private float[] _rot = null!;
        private int _cap;
        private bool _cull;
        private WorldRect _view;
        private int _lodStride = 1;
        private float _beltScroll;

        /// <summary>已写出的实例数（快照的 LiveCount）。</summary>
        public int Live;

        /// <summary>被剔除掉的实例数（诊断与剔除率）。</summary>
        public int Culled;

        /// <summary>带上物品的**真实件数**（不因剔除而变化）。</summary>
        public int ItemCount;

        /// <summary>一个格的倍数（多格建筑才不是 1，见 <see cref="Scale"/> 的注释）。</summary>
        private const float OneCell = 1f;

        public SnapshotWriter(FactorySim f) => _f = f;

        /// <summary>开始写一帧：取缓冲、定剔除范围与 LOD 步长。</summary>
        public void Begin(RenderSnapshot s)
        {
            _xy = s.Xy;
            _sprite = s.SpriteId;
            _tint = s.Tint;
            _scale = s.Scale;
            _scroll = s.Scroll;
            _scrollAxis = s.ScrollAxis;
            _rot = s.Rot;
            _cap = s.Capacity;
            Live = 0;
            Culled = 0;
            ItemCount = 0;

            // 带面滚动相位：见 BeltScrollPhase。带子本身是静态瓦片，靠这个相位滚动 UV 才有流动感。
            _beltScroll = _f.BeltScrollPhase;

            // ── 剔除：取可见范围 ────────────────────────────────────────────
            // ⚠ **剔除只影响「写多少实例」，不影响任何模拟状态** ——
            //   它不进 StateHash，也不改变带推进/机器/电力/流体的任何决策。
            //   所以同一份布局在不同视口下模拟结果完全相同，只有画出来的东西不同。
            //   没推送过视口时（TryGet=false）**不剔除**：宁可多画，也不能因为
            //   「还不知道视口」就画出一片空白。
            WorldRect view = default;
            float viewScale = 1f;
            bool cull = false;
            if (_f.CullEnabled) cull = _f.CullViewport.TryGet(out view, out viewScale);
            _cull = cull;
            _view = view;

            // ⚠ **亚像素时不能「全剔掉」** —— 那样拉远就什么都看不见了。
            //   正确做法是 **LOD 抽样**：每 stride×stride 格只画一格。
            _lodStride = _f.LodStrideFor(viewScale);
            _f.LodStride = _lodStride;

            if (cull)
            {
                // 向外放一格余量：物块的中心可能在视口外、身子还压着视口边缘。
                // 少了这条余量，边缘上的实例会随相机移动**闪空**（比不剔除更难看）。
                const float margin = FactoryLayout.CellPx;
                _view = new WorldRect(view.LoX - margin, view.LoY - margin,
                                      view.HiX + margin, view.HiY + margin);
            }
        }

        private bool InView(float x, float y)
            => x >= _view.LoX && x <= _view.HiX && y >= _view.LoY && y <= _view.HiY;

        /// <summary>① 某一层的带面：每格一张 32px 瓦片（低层先写，高层后写 ⇒ 高层压住低层）。</summary>
        public void BeltBases(int level)
        {
            if (!_f.RenderBeltBases) return;
            FactorySim f = _f;
            float[] xy = _xy;
            int cap = _cap;

            for (int b = 0; b < f._beltSlots && Live < cap; b++)
            {
                if (!f._beltAlive[b]) continue;
                BeltDef def = f._layout.BeltAt(b)!;
                // 只画这一层：另一层的带由那一趟画（顺序 = 层号升序，就是遮挡顺序）
                if (FactoryLayout.NormalizeLevel(def.Level) != level) continue;

                // 整段带都在视口外 ⇒ 一次比较跳过它全部格子（这是剔除省下的主要部分）。
                if (_cull && !_view.Intersects(f._beltBounds[b]))
                {
                    Culled += def.Cells.Length;
                    continue;
                }

                GridPos[] cells = def.Cells;
                ushort[]? sprites = f._beltCellSprites[b];
                float[]? rots = f._beltCellRot[b];
                // LOD 只做**抽样**：**按 s 步进**遍历（不然光「走一遍 145 万格」就要 29ms），
                // 再用**路径下标**决定画不画（`k % s² == 0` ⇒ 每 s×s 格留一格）。
                // ⚠ 判据必须落在**路径下标**上，不能落在**坐标点阵**上：坐标点阵只在
                //   「这一道的坐标恰好是 s 的倍数」时才留格，于是**整条道会一条条消失**
                //   （实测整图取景只剩 6,394 个实例，屏幕上几乎什么都没有）。
                //   用路径下标则**每条道都还在**（变成点线），厂区走向一眼看得出。
                // **照真实瓦片画** —— 不要加「缩略图」式画法（放大成一片 / 素色块 / 拉成一条线）。
                int lodStep = Math.Max(1, _lodStride);
                int lodEmit = lodStep * lodStep;
                for (int k = 0; k < cells.Length && Live < cap; k += lodStep)
                {
                    // ⚠ 地下段**不画带面** —— 那几格要让给横穿过去的那条带。
                    //   物品照常从这儿过（只是这一层看不见），所以模拟与吞吐都不受影响。
                    if (def.IsTunnelCell(k)) { Culled++; continue; }

                    if (lodStep > 1 && (k % lodEmit) != 0) { Culled++; continue; }

                    (float cx, float cy) = FactoryLayout.GridToWorld(cells[k]);
                    if (_cull && !InView(cx, cy)) { Culled++; continue; }

                    xy[Live * 2] = cx;
                    xy[Live * 2 + 1] = cy;
                    _sprite[Live] = sprites != null && k < sprites.Length ? sprites[k] : SpriteBelt;
                    _tint[Live] = 0xFFFFFFFF;
                    _scale[Live] = OneCell;
                    // 朝向：往西/往北的直线段转 180°（见 BeltTileAt）—— 箭头对着流向。
                    float cellRot = rots != null && k < rots.Length ? rots[k] : 0f;
                    _rot[Live] = cellRot;
                    // 纹路沿**这一格的流向轴**滚（轴由 BeltFlow 给），而且要和物品走同一方向：
                    //   · 转过的格子（往西/往北的直线）：旋转本身已经把纹路方向翻过来了，
                    //     再取反一次会正好翻回去（否则箭头朝西、纹路却朝东）；
                    //   · 没转的格子（正向直线 / 拐角）：用 1−p 把纹路反过来（fract 自动回卷）。
                    (float axisX, bool forward) = BeltFlow(def, k);
                    _scroll[Live] = cellRot != 0f || forward ? _beltScroll : 1f - _beltScroll;
                    // ⚠ **拐角格把轴置 0 = 「是带面但静止」**（着色器按 `abs(axis) > 0.5` 判）。
                    //   拐角瓦片的图案是弧形、不可重复滚动，滚它会让拐角与两侧直线段错位。
                    _scrollAxis[Live] = BeltCellIsCorner(cells, k) ? 0f : axisX;
                    Live++;          // ⚠ 循环里有 `continue`，所以 `Live++` 必须在写出实例的分支里补 ——
                    //   漏掉它会让**带面瓦片全部不计数**，
                    //   于是 LiveCount 只有物品数（实测 1,008,000 而不是 2,008,000），
                    //   画面上一整层带面消失，而且不报任何错。
                }
            }
        }

        /// <summary>①′ 管道：与低层带面同层（都在物品下面）。管道没有逐格模拟状态，纯渲染。</summary>
        public void Pipes()
        {
            FactorySim f = _f;
            for (int i = 0; i < f._layout.PipeSlotCount && Live < _cap; i++)
            {
                FluidPipeDef? pipe = f._layout.PipeAt(i);
                if (pipe == null) continue;
                // 朝向由**路径**决定（与带同一套做法，见 BeltTileAt）：占位瓦片是**竖**的细灰条，
                // 所以横着走的那段要转 90° —— 否则横向管网看着像一排立着的小棍（用户的「看着不对」）。
                // ⚠ 管道是**管网**不是一进一出的路径：两段横竖相交时会画出两个实例（各转各的），
                //   叠起来正好是个十字 —— 这是这套占位美术能给出的最好效果；真正的 T/十字
                //   仍然需要一套真管道素材或程序化管道。
                float pipeRot = PipeRotation(pipe);
                // 抽样**按管道上的格序号**，与带面那段完全同一套做法。
                // ⚠ **不要**改成坐标取模抽样（`LodSkip(cell.X, cell.Y)`）—— 正是下面那句注释禁止的做法：
                //   竖直管道每一格的 X 都相同，只要这个 X 不被步长整除，**整条管道会被全部抽掉**
                //   （横向管道则是 Y）⇒ 画面上整条管网凭空消失，且不报任何错。
                GridPos[] pipeCells = pipe.Cells;
                int lodStep = Math.Max(1, _lodStride);
                int lodEmit = lodStep * lodStep;
                for (int k = 0; k < pipeCells.Length && Live < _cap; k += lodStep)
                {
                    // 同样只按走的格数抽，不看坐标点阵。
                    if (lodStep > 1 && (k % lodEmit) != 0) { Culled++; continue; }
                    (float px, float py) = FactoryLayout.GridToWorld(pipeCells[k]);
                    if (_cull && !InView(px, py)) { Culled++; continue; }

                    _xy[Live * 2] = px;
                    _xy[Live * 2 + 1] = py;
                    _sprite[Live] = SpritePipe;
                    // ⚠ 管道是**占位美术**：(15,0) 那格实测是**机器附件**的瓦片，整格画出来
                    //   就是一大块半透明色块。所以这里把实例缩成**半格**，压成一条明显的连接线：
                    //   至少能读出管网走向，而不再像一块 UI 面板。
                    //   （下面的 tint 是白色，而本项目**关闭了逐实例颜色通路**
                    //    ⇒ 颜色实际不生效，真正起作用的是 `_scale = 0.5f`。）
                    _tint[Live] = 0xFFFFFFFF;
                    _scale[Live] = 0.5f;
                    _scroll[Live] = -1f;
                    _rot[Live] = pipeRot;
                    Live++;
                }
            }
        }

        /// <summary>② 机器层：每台一个实例（该机器的内容框已把内容居中，尺寸表把它缩进一格内）。</summary>
        public void Machines()
        {
            FactorySim f = _f;
            for (int m = 0; m < f._machineSlots && Live < _cap; m++)
            {
                if (!f._machineAlive[m]) continue;
                MachineDef def = f._layout.MachineAt(m)!;
                (float mx, float my) = def.WorldCenter;
                if (_cull && !InView(mx, my)) { Culled++; continue; }
                // 机器也按**数量**抽样（不看坐标点阵），并把留下的那台放大成 lodStride 格 ——
                // 于是拉远时机器还是一片片可见的色块，而不是「坐标不被整除就整台消失」。
                if (_lodStride > 1 && (m % _lodStride) != 0) { Culled++; continue; }

                ushort cell = MachineFrameCell(def.SpriteCell, def.FrameStride, f.MachineFrameOf(m));

                _xy[Live * 2] = mx;
                _xy[Live * 2 + 1] = my;
                _sprite[Live] = cell;                        // 照真实美术画（不换素色块）
                _tint[Live] = 0xFFFFFFFF;
                // 多格建筑按占地边长等比放大（2×2 ⇒ 2 格）。占地是功能（占用位图），
                // 尺寸是外观；两者由同一个 SizeX/SizeY 派生，而且规则只有一处（MachineScale）。
                _scale[Live] = MachineScale(def);
                _scroll[Live] = -1f;
                _rot[Live] = FacingRotation(def);            // 朝向：0=东 1=南 2=西 3=北
                Live++;
            }
        }

        /// <summary>③ **某一层**带上物品：把每条带的 gap 前缀和走一遍（渲染构造的 O(可见物品数) 固有成本）。</summary>
        public void Items(int level)
        {
            FactorySim f = _f;
            // ⚠ `ItemsOnBelts` 报的是**带上的真实件数**（HUD 的「带上物品」、探针 14 的判据都读它），
            //   所以它**不因剔除而变化** —— 剔除只决定「画多少个」，不改变「有多少个」。
            //   这也让「剔除没写坏东西」可验证：同一场景开/关剔除，ItemsOnBelts 必须完全相等。
            //   ⚠ 分层之后它仍然只累计**一次**（每层各走一半的带），不会被算成两倍。
            for (int b = 0; b < f._beltSlots; b++)
            {
                if (!f._beltAlive[b]) continue;
                BeltDef def = f._layout.BeltAt(b)!;
                if (FactoryLayout.NormalizeLevel(def.Level) != level) continue;

                GapLine belt = f._belts[b]!;
                int n = belt.Count;
                if (n == 0) continue;

                // 整段带都在视口外：跳过**前缀和**这一整趟（那才是这里的成本所在），
                // 但件数照样按真实值累计 —— belt.Count 是 O(1) 的。
                // 这条让「百万件、屏幕只看得到一小块」这一最常见的情形真正拿到数量级的收益：
                // 否则我们仍要对全部 100 万件做前缀和，只不过不写缓冲而已。
                if (_cull && !_view.Intersects(f._beltBounds[b]))
                {
                    ItemCount += n;
                    Culled += n;
                    continue;
                }

                // ⚠ **拉远（一格不足一个像素）时干脆不画带上物品**：
                //   物品是全场最贵的一层：成本不在写缓冲，而在**前缀和那一趟**（逐件 `GapAt`），
                //   整图取景时 100 万件全走一遍就是 30ms 量级。而那个倍率下每件连一个像素都占不到，
                //   画出来本来也只是噪点。
                //   ⚠ 件数**照旧按 `belt.Count` 累计**（HUD 的「带上物品」与探针 14 的判据都读它）
                //     —— 剔除只决定画多少，不改变「有多少」。
                if (_lodStride > 1)
                {
                    ItemCount += n;
                    Culled += n;
                    continue;
                }

                int lengthSub = belt.LengthSub;

                int acc = 0;
                for (int i = 0; i < n; i++)
                {
                    acc += belt.GapAt(i);
                    ItemCount++;

                    // 逐件剔除：带是斜穿视口的（或很长的横/竖带）时，段级包围盒相交但
                    // 大部分物品仍在视口外。位置先算出来才能判，所以这一步省不掉 ——
                    // 省掉的是「写进缓冲」以及后面 SetBuffer 的搬运量。
                    BeltPointAt(def, (lengthSub - acc) * PxPerSub, out float ix, out float iy);
                    if (_cull && !InView(ix, iy)) { Culled++; continue; }
                    if (Live >= _cap) { Culled++; continue; }

                    _xy[Live * 2] = ix;
                    _xy[Live * 2 + 1] = iy;

                    // 带上物品的类型 → 内容表的物品图标（模拟只认物品 id，映射是内容层的职责）
                    int t = belt.TypeOf(i);
                    // ⚠ 内容表约定 0 = 空；`(t - 1)` 在 t == 0 时是 **-1**，取模之后仍是负数 ⇒ 越界。
                    //   今天带上不会有 0 号物品，但别让那条不变式成为这里唯一的防线。
                    if (t <= 0) t = 1;
                    int icon = f._content.IsKnownItem(t)
                        ? f._content.ItemIcon(t)
                        : ItemIconCells[(t - 1) % ItemIconCells.Length];
                    _sprite[Live] = (ushort)icon;
                    _tint[Live] = 0xFFFFFFFF;
                    _scale[Live] = OneCell;
                    _scroll[Live] = -1f;
                    _rot[Live] = 0f;
                    Live++;
                }
            }
        }
    }

    /// <summary>世界点是否落在（已放宽余量的）可见矩形内。剔除用，见 <see cref="CullEnabled"/>。</summary>
    private static bool InView(WorldRect view, float x, float y)
        => x >= view.LoX && x <= view.HiX && y >= view.LoY && y <= view.HiY;

    /// <summary>
    /// 多格建筑渲染时的**世界中心**：左上格 + 占地一半的偏移。
    ///
    /// ⚠ **只此一份**：<see cref="MachineDef.WorldCenter"/>（正式渲染）与蓝图 / 框选 / 幽灵预览
    ///   都走它 —— 预览各写一遍的话，多格建筑在预览里会与放下去之后**差半格**，
    ///   而画面上很难判断到底是谁错。
    ///   收的是**格坐标**（不是世界坐标），这样调用方无论手里是 <c>def.Pos</c> 还是
    ///   「光标格 + 剪贴板相对坐标」都能用，且算式与原来的逐位一致。
    /// </summary>
    public static (float X, float Y) MachineCenterAt(float cellX, float cellY, MachineDef def)
    {
        float cx = cellX + (def.SizeX - 1) * 0.5f;
        float cy = cellY + (def.SizeY - 1) * 0.5f;
        return (cx * FactoryLayout.CellPx + FactoryLayout.CellPx * 0.5f,
                cy * FactoryLayout.CellPx + FactoryLayout.CellPx * 0.5f);
    }

    /// <summary>多格建筑按**占地边长**等比放大（2×2 ⇒ 2 格）；1 格 ⇒ 1.0（不放大）。</summary>
    public static float MachineScale(MachineDef def)
    {
        int span = Math.Max(def.SizeX, def.SizeY);
        return span > 1 ? span : 1f;
    }

    /// <summary>朝向 → 实例旋转角（0=东 1=南 2=西 3=北，每档 90°）。</summary>
    public static float FacingRotation(int facing) => facing * (MathF.PI / 2f);

    /// <inheritdoc cref="FacingRotation(int)"/>
    public static float FacingRotation(MachineDef def) => FacingRotation(def.Facing);

    /// <summary>
    /// 管道占位瓦片是**竖**的细灰条 ⇒ 横着走的那段要转 90°，否则横向管网像一排立着的小棍。
    /// ⚠ 正式渲染与预览共用它。
    /// </summary>
    public static float PipeRotation(FluidPipeDef pipe)
        => pipe.Cells.Length >= 2 && pipe.Cells[0].Y == pipe.Cells[^1].Y ? MathF.PI / 2f : 0f;

    /// <summary>
    /// 机器该显示第几帧。**由加工进度驱动，而不是固定 tick 分频**：
    /// 一个加工周期恰好循环一次 <see cref="MaxMachineFrames"/> 帧。
    ///
    /// 为什么这样做：
    ///   - 固定「每 8 tick 一帧」时，32 tick 走完一轮而加工要 60 tick ⇒ 一轮没走完就被重置，**画面上会跳帧**。
    ///   - 进度驱动之后动画与加工天然同步：**欠供电时加工变慢，画面上的运转也跟着变慢** ——
    ///     这样电力系统就有了直观的视觉反馈，不用额外做 UI。
    ///   - 非加工状态固定回第 0 帧（空闲的机器不显示运转）。
    /// </summary>
    private int MachineFrameOf(int m)
    {
        MachineDef def = _layout.MachineAt(m)!;
        // ⚠ 帧数取**这台机器自己的** FrameCount，不取全局 MaxMachineFrames —— 见 MachineDef.FrameCount。
        if (def.FrameStride <= 0 || def.FrameCount <= 1) return 0;   // 静态贴图，不必算
        if ((MachineState)_state[m] != MachineState.Working) return 0;

        int span = _work[m] * SatFull;
        if (span <= 0) return 0;

        int done = span - _progress[m];
        if (done < 0) done = 0;
        else if (done > span) done = span;

        return (int)((long)done * def.FrameCount / span) % def.FrameCount;
    }

}
