namespace ForgeFlow.Sim;

/// <summary>机器种类。决定它有几条输入带 / 输出带。</summary>
public enum MachineKind : byte
{
    /// <summary>纯生产机：没有输入带，按 <see cref="MachineDef.WorkTicks"/> 不断产出。</summary>
    Producer = 0,

    /// <summary>加工机：一进一出。</summary>
    Processor = 1,

    /// <summary>出货机：没有输出带，产出即计入 <c>ShippedTotal</c>。</summary>
    Shipper = 2,

    /// <summary>
    /// 分流器：**一进多出**，按轮转（round-robin）把物品依次送往各条输出带。
    ///
    /// 之所以做成独立机器而不是让一条带自己分叉：
    /// 带是 1-lane（单入单出），「一条带同时通往两处」在物理上就没有意义。
    /// 分叉必须由一台有状态的机器来承担 —— 而「有状态」正是轮转能保持确定性的原因。
    /// </summary>
    Splitter = 3,

    /// <summary>
    /// 合流器：**多进一出**，按输入带 id 升序取料（确定性），任一条有货就能工作。
    /// </summary>
    Merger = 4,

    /// <summary>
    /// 流体源：每 tick 往**相邻管网**注入 <see cref="MachineDef.FluidRate"/> 单位流体。
    /// </summary>
    FluidSource = 5,

    /// <summary>
    /// 流体消耗机：每做完一件要消耗 <see cref="MachineDef.FluidPerItem"/> 单位流体；
    /// 管网里不够就**停下等**（不是降速），等流体补上再继续。
    /// </summary>
    FluidUser = 6,

    /// <summary>
    /// 仓储箱：**缓冲**。上游的什么都收（不限种类），下游按先进先出出货。
    /// 它的价值是让生产与消耗解耦 —— 之前只有「带」，带一满上游就全线停摆。
    /// </summary>
    Storage = 7,

    /// <summary>
    /// 长臂机械臂：**中继 + 半径 2 的抓取距离**。
    ///
    /// 一般机器**只能直接贴带**：带的端点必须紧挨机器才算接上。
    /// 机械臂打破它 —— 带端点离它**两格**也算接上（见 <see cref="FactoryLayout.Resolve"/> 的
    /// <c>Connect</c>），所以它可以跨过一格空地搬运，把两段本来接不上的带连起来。
    /// 行为上它就是一台有工时的透传机器（无配方 ⇒ 走老路径）。
    /// </summary>
    Inserter = 8,

    /// <summary>
    /// 均压泵：把流体在**两个相邻管网之间**搬运（从存量多的搬向存量少的，速率受限）。
    ///
    /// 为什么需要它：管网是「相邻管道格子的连通分量」⇒ **两个管网永远不直接相邻**
    /// （相邻就合并了）。所以泵是唯一的跨网通道 —— 它占的是机器格，不是管道格。
    /// </summary>
    Pump = 9,
}

/// <summary>
/// 网格坐标（原点在左上，y 向下）。**布局只认网格，不认像素** ——
/// 像素位置由 <see cref="FactoryLayout.GridToWorld"/> 派生，
/// 所以「摆在哪」是美术/相机的事，与模拟无关。
/// </summary>
public readonly record struct GridPos(int X, int Y);

/// <summary>
/// **一台机器需要接哪些轨道** —— 任何机器都要绑定轨道才能使用，
/// 要不要输入 / 输出由机器类型决定。
///
/// 这是**玩法规则**，所以它只有一处定义：模拟用它判「接够了吗」，UI 用它显示「还缺什么」。
/// 机器只认**显式绑定**（<see cref="MachineDef.InBeltBindings"/> / <see cref="MachineDef.OutBeltBindings"/>），
/// **没有几何兜底** —— 没接够的机器就是不工作（生产者产不出、加工机永远等输入、出货机永远没货）。
///
/// ⚠ 流体机器（水塔 / 均压泵）既不吃带也不出带 ⇒ 不需要任何轨道角色：
///   水塔是管网源、泵在**两个管网**之间搬运（<see cref="MachineDef.FluidNetwork"/>）。
/// </summary>
public static class MachineWiring
{
    /// <summary>这台机器需要**至少一条输入轨**吗。</summary>
    public static bool NeedsInput(MachineKind kind) => kind switch
    {
        MachineKind.Shipper => true,          // 出货机：只有输入（产出即出货）
        MachineKind.Producer => false,        // 矿机：没有输入带，只有产出
        MachineKind.FluidSource => false,     // 水塔：往管网注水
        MachineKind.Pump => false,            // 均压泵：在两个管网之间搬
        _ => true,                            // 加工/分流/合流/仓储/机械臂/耗水机 都要吃带
    };

    /// <summary>这台机器需要**至少一条输出轨**吗。</summary>
    public static bool NeedsOutput(MachineKind kind) => kind switch
    {
        MachineKind.Producer => true,         // 矿机：只有产出
        MachineKind.Shipper => false,         // 出货机：没有输出带
        MachineKind.FluidSource => false,
        MachineKind.Pump => false,
        _ => true,
    };
}

/// <summary>一台机器。</summary>
public sealed class MachineDef
{
    public required MachineKind Kind { get; init; }

    /// <summary>**占用范围的左上格**。多格建筑由 <see cref="SizeX"/>/<see cref="SizeY"/> 决定。</summary>
    public required GridPos Pos { get; init; }

    public required int WorkTicks { get; init; }

    /// <summary>所属电网编号（同号即同网）。</summary>
    public required int NetworkId { get; init; }

    /// <summary>生产者初始携带的物品类型。</summary>
    public ushort InitialItemType { get; init; } = 1;

    /// <summary>横向占几格（≥1）。</summary>
    public int SizeX { get; init; } = 1;

    /// <summary>纵向占几格（≥1）。</summary>
    public int SizeY { get; init; } = 1;

    /// <summary>流体源的注入速率（单位/tick）。</summary>
    public int FluidRate { get; init; }

    /// <summary>流体消耗机每做一件消耗多少单位。</summary>
    public int FluidPerItem { get; init; }

    /// <summary>
    /// 机器进图集的精灵编号（渲染层用）。
    ///
    /// 它是**可建造项带来的美术身份** —— 同一 <see cref="MachineKind"/> 的不同配方
    /// （铁熔炉 / 铜熔炉）长得不一样，所以美术不能由 Kind 反推，必须随机器一起存下来。
    /// 由 <see cref="BuildableDef.Art"/> 填，默认 = 经典蓝机器。
    /// </summary>
    public int SpriteCell { get; init; } = 151;   // 行 15 列 1

    /// <summary>动画帧在图上相隔几行（0 = 静态贴图）。这套美术只有分流器那台有 4 帧。</summary>
    public int FrameStride { get; init; }

    /// <summary>
    /// 这台机器有几帧动画（1 = 静态）。
    ///
    /// ⚠ 必须**随机器存下来**，不能拿全局的 <see cref="FactorySim.MaxMachineFrames"/> 当帧数：
    ///   那个常量的含义是「按 <see cref="FrameStride"/> 推算时的**默认**帧数」
    ///   （见 <see cref="MachineArt.FrameCount"/>）。一旦有美术显式给了 <c>FrameYs</c>（帧数 ≠ 4），
    ///   按那个全局常量取模就会取到**根本没被填过的图集格** —— 画出来是邻格的图，
    ///   既不报错、也不影响「能不能画出来」，只让画面静默错掉。
    /// </summary>
    public int FrameCount { get; init; } = 1;

    /// <summary><see cref="ContentDb.Recipes"/> 的下标；**-1 = 无配方**（走「透传」老路径）。</summary>
    public int RecipeIndex { get; init; } = -1;

    /// <summary>仓储箱槽位数（<see cref="MachineKind.Storage"/> 用）。</summary>
    public int StorageCapacity { get; init; }

    /// <summary>
    /// 朝向，0..3（东 / 南 / 西 / 北）。**纯外观** —— 连接是按几何推导的，
    /// 与朝向无关；玩家看得见的好处是同一台机器可以摆出不同方向。
    /// </summary>
    public int Facing { get; init; }

    /// <summary>
    /// 第二个相邻管网编号（泵用；其余机器恒为 -1）。
    /// 泵必须同时贴着两个不同的管网才有的搬，所以单个 <see cref="FluidNetwork"/> 不够。
    /// </summary>
    public int FluidNetworkB { get; internal set; } = -1;

    /// <summary>
    /// 相邻管网编号（由 <see cref="FactoryLayout.Resolve"/> 按几何推导，-1 = 没接上管网）。
    /// 与带的上下游同理 —— **每次 Resolve 重新贴边**，所以「先铺管、后放机器」也能连上。
    /// </summary>
    public int FluidNetwork { get; internal set; } = -1;

    /// <summary>占用范围的中心（世界坐标用）。多格建筑靠它定位，渲染与占用用的是同一套。</summary>
    public (float X, float Y) WorldCenter => FactorySim.MachineCenterAt(Pos.X, Pos.Y, this);

    /// <summary>枚举占用到的每一格。</summary>
    public IEnumerable<GridPos> Footprint()
    {
        int sx = SizeX < 1 ? 1 : SizeX;
        int sy = SizeY < 1 ? 1 : SizeY;
        for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
                yield return new GridPos(Pos.X + x, Pos.Y + y);
    }

    /// <summary>
    /// 复制一份挪到别处（**蓝图粘贴用**）。除位置 / 朝向 / 电网之外逐字段照搬 ——
    /// 这样粘贴出来的机器与原件在功能与外观上完全一致，不会有「漏抄某个字段」的暗坑。
    ///
    /// ⚠ **不抄 <see cref="InBeltBindings"/> / <see cref="OutBeltBindings"/>**：那是**带 id**，
    ///   而粘出来的是一份新位置上的东西，带 id 既没意义也不一定还存在（默认空 = 没接）。
    /// </summary>
    public MachineDef MovedTo(GridPos pos, int facing, int networkId) => new()
    {
        Kind = Kind,
        Pos = pos,
        WorkTicks = WorkTicks,
        NetworkId = networkId,
        InitialItemType = InitialItemType,
        SizeX = SizeX,
        SizeY = SizeY,
        FluidRate = FluidRate,
        FluidPerItem = FluidPerItem,
        SpriteCell = SpriteCell,
        FrameStride = FrameStride,
        FrameCount = FrameCount,          // ⚠ 漏了它 ⇒ 粘贴出来的动画机器会变成静态
        RecipeIndex = RecipeIndex,
        StorageCapacity = StorageCapacity,
        Facing = facing,
    };

    // 下面两项由 FactoryLayout.Resolve() 从带的连接关系推出来，不要手填。
    //
    // ⚠ 是**数组**而不是单个 int：分流器一进多出、合流器多进一出。
    //   元素顺序 = 带 id 升序（Resolve 按 id 遍历得到的），所以是确定的 ——
    //   这正是「合流器取哪条带的料」「分流器先送哪条」能保持确定性的依据。
    //   普通机器（一进一出）的数组长度就是 0 或 1，行为与之前完全一致。
    public int[] InBelts { get; internal set; } = Array.Empty<int>();
    public int[] OutBelts { get; internal set; } = Array.Empty<int>();

    /// <summary>
    /// **玩家显式绑定的输入带 id 集合**（升序、去重；空 = 没绑）。
    ///
    /// 为什么是**集合**而不是一个 id：分流器是「1 进 2 出」、合流器是「2 进 1 出」——
    /// 它们天生就要接多条带。只留一个 id 的话，这类机器就再也接不出第二条，
    /// 而机器必须绑定轨道才能用 ⇒ 它们直接不能用了。
    ///
    /// 为什么必须有显式绑定 —— 几何邻接有两个治不好的盲区，而「轨道可以交错」把第二个放大了：
    ///   ① 一台机器旁边可能挨着**好几条**带（交叉、并行、多段带），几何只能挑一条；
    ///   ② 几何只看带的**首/尾两格**，带从机器旁边**中段擦过**时它根本发现不了
    ///      ⇒ 那条带永远接不上这台机器。
    /// ⇒ 玩家点机器选一条带（UI 见 WalkingSkeleton 的轨道选择器）就是唯一的接线方式。
    ///
    /// ⚠ **机器必须接线才能工作**：模拟只认这两张表，**没有几何兜底**。
    ///   需要哪些角色由机器种类决定（见 <see cref="MachineWiring"/>）；没接够 = 不工作。
    ///   场景/测试里那些「按几何摆好的」布局请显式调 <see cref="FactoryLayout.AutoBindGeometrically"/>
    ///   —— 那是**布局构造者的便利**，不是模拟的规则。
    /// ⚠ 带被拆掉时绑定会一起清掉（见 <see cref="FactoryLayout.RemoveBelt"/>）——
    ///   槽位空洞是会被**复用**的，不清就会"继承"上一条带的绑定。
    /// </summary>
    public int[] InBeltBindings { get; internal set; } = Array.Empty<int>();

    /// <summary>**玩家显式绑定的输出带 id 集合**（升序、去重；空 = 没绑）。见 <see cref="InBeltBindings"/>。</summary>
    public int[] OutBeltBindings { get; internal set; } = Array.Empty<int>();

    /// <summary>这条带是不是本机器绑定的输入轨。</summary>
    public bool IsBoundInput(int beltId) => Array.IndexOf(InBeltBindings, beltId) >= 0;

    /// <summary>这条带是不是本机器绑定的输出轨。</summary>
    public bool IsBoundOutput(int beltId) => Array.IndexOf(OutBeltBindings, beltId) >= 0;

    /// <summary>这台机器的**必要角色都接上了**吗（不需要的角色不计）。见 <see cref="MachineWiring"/>。</summary>
    public bool IsWired
    {
        get
        {
            if (MachineWiring.NeedsInput(Kind) && InBeltBindings.Length == 0) return false;
            if (MachineWiring.NeedsOutput(Kind) && OutBeltBindings.Length == 0) return false;
            return true;
        }
    }
}

/// <summary>
/// 一段带：**有序的格子路径**。
/// 之所以是路径而不是「一条直线」，是因为拐角/T/十字马上就要用到 ——
/// 这套美术的 4 向连通瓦片已经就位，把路径先做对，自动铺瓦才有东西可铺。
/// </summary>
public sealed class BeltDef
{
    /// <summary>有序格子：<c>Cells[0]</c> 是入口（上游），最后一个是出口（下游）。</summary>
    public required GridPos[] Cells { get; init; }

    /// <summary>
    /// **轨道层**：0 = 低轨（默认，贴地），1 = 高轨（架在上面）。
    ///
    /// 为什么要有它：两条带走成十字时必须分得出上下 —— 同一格允许被**不同层**的两条带共用
    /// （见 <see cref="OccupancyGrid.CanPlaceBelt(GridPos, int)"/>），高轨画在低轨**之后**，
    /// 于是低轨的瓦片与**它上面的物品**都被高轨挡住（见 FactorySim.WriteSnapshotCore 的分层顺序）。
    /// 高轨自己照常跑、照常被看见。
    ///
    /// ⚠ 层是**整条带**的属性，不是逐格的：逐格层会让占用、铺瓦、快照分层、蓝图与撤销
    ///   全都多一套分支，而「一条带一段高一段低」本项目没有需求（要那样就铺两条带）。
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// 上游机器 id；-1 = 没有（玩家/外部投料）。
    ///
    /// ⚠ **由 <see cref="FactoryLayout.Resolve"/> 按几何推导出来，不要手填、也不要依赖建带时的值。**
    /// 理由：玩家完全可能**先铺带、后建机器**（顺序不该被约束）。建带时推断一次就冻结的话，
    /// 后建的机器永远接不上 —— 这个坑是探针 06 从零建产线时抓到的：
    /// 带铺好了、机器也建了，但带的下游一直是「空」，整条线不出货。
    /// </summary>
    public int FromMachine { get; internal set; } = -1;

    /// <summary>下游机器 id；-1 = 出货到虚空。同样由 <see cref="FactoryLayout.Resolve"/> 推导。</summary>
    public int ToMachine { get; internal set; } = -1;

    /// <summary>
    /// **下游那段带**的 id；-1 = 没有。由 <see cref="FactoryLayout.Resolve"/> 按几何推导：
    /// 本段带的**出口格**与另一段带的**入口格**边相邻就接上（取 id 最小的那段）。
    ///
    /// ⚠ 没有这个连接时**带与带之间是不连通的** —— 一条带必须是「机器到机器」的完整路径，
    ///   想绕弯只能画成一条带。有这个连接之后玩家可以一段一段画、拐角接拐角，
    ///   斜拖自动拆出的两段直角带也才真正连得上（否则第二段收不到货，整条线静默断掉）。
    /// </summary>
    public int ToBelt { get; internal set; } = -1;

    /// <summary>上游那段带的 id；-1 = 没有（玩家投料）。与 <see cref="ToBelt"/> 互为反向。</summary>
    public int FromBelt { get; internal set; } = -1;

    /// <summary>
    /// **地下段**：<c>Cells[TunnelFrom .. TunnelTo)</c> 这几格是「在地下走的」，默认 -1 = 没有。
    ///
    /// 这是「**轨道与轨道交错**」的实现方式（其他工厂游戏里的地下带 / 天桥）：
    ///   · 这几格**不画带面**（画面上一眼看不出带从这儿过）；
    ///   · 这几格**不占位**（占用位图不标记）⇒ **另一条带可以正大光明地横穿过去**。
    ///
    /// 但它**仍是同一条带的一部分**：路径连续、间距模型照常（物品从入口一路走到出口，
    /// 只是中途被画在「看不见的层」上）。所以模拟侧**一行都不用改** ——
    /// `BeltPointAt`、前缀和、压缩游标、吞吐全都与普通带完全一致。
    ///
    /// ⚠ 为什么不做成「两格带 + 中间断开」：那样 `BeltPointAt` 会把物品挤在出口
    ///   （路径只剩两格，插值退化成阶跃），而且带的容量会掉到 2 件。
    ///   保留完整路径但**只隐藏渲染与占位**，才是改动最小、语义最干净的做法。
    /// </summary>
    public int TunnelFrom { get; init; } = -1;
    public int TunnelTo { get; init; } = -1;

    /// <summary>
    /// **逐格**地下掩码（长度可与 <see cref="Cells"/> 不同，短了按 false 算）。
    ///
    /// ⚠ 为什么需要它、而不只是一个区间：一条蛇形带会**反复穿过**同一条总线
    ///   （8 道 = 8 次），而 <see cref="TunnelFrom"/>/<see cref="TunnelTo"/> 只能表达
    ///   **一段连续区间** —— 用区间时只有最后一次匹配生效、前面几次全漏掉，
    ///   自检实测 **14,950 格重叠**。掩码则天然支持任意多处。
    /// </summary>
    public bool[]? TunnelMask { get; init; }

    /// <summary>第 <paramref name="k"/> 格是否在地下（不画、不占位）。</summary>
    public bool IsTunnelCell(int k)
    {
        if (TunnelMask != null) return k < TunnelMask.Length && TunnelMask[k];
        return TunnelFrom >= 0 && k >= TunnelFrom && k < TunnelTo;
    }
}

/// <summary>
/// 一段管道：**有序的格子路径**（和带一样，便于拐角/自动铺瓦）。
///
/// ⚠ 注意：**管网 ≠ 一段管道**。管网是所有**相邻管道格子**的连通分量 ——
/// 玩家可以分几次铺管道，只要它们边相邻就属于同一个管网。
/// 这与「带」是不同的模型：带永远是独立的一进一出路径，而管网是一个共享汇量。
/// </summary>
public sealed class FluidPipeDef
{
    public required GridPos[] Cells { get; init; }
}

/// <summary>
/// 工厂拓扑 —— **数据，不是代码**；而且是**可变**的：
/// 玩家要能随时增删机器与带，不是一份由构造函数生成、改不动的链式结构。
///
/// ══ 为什么要「稳定 id + 空洞」而不是「数组 + 删除时重排」 ══════════════════
///
/// 工厂模拟的状态（机器进度、带上物品、电网需求）是**按下标平行存储**的。
/// 如果删除一台机器就把后面的全部前移，那么每次结构性改动都会让所有状态错位 ——
/// 要么整份重算（丢掉正在加工的进度），要么维护一张重映射表（易错）。
///
/// 所以这里用**稳定 id**：增删只置空/填洞，id 永不变动。
///   - 遍历仍然**按 id 升序** ⇒ 确定性不受影响（这正是「活跃位图 + id 序」的前提）；
///   - 删除 O(1)，插入 O(1)（复用空洞）；
///   - 每 tick 成本只随**存活**机器数增长，与总历史无关。
///
/// ⇒ 稳定 id 足够用：ECS 的结构性变更优势针对的是「每帧新增成千上万实体」，
///    而工厂游戏里玩家每秒最多放几个建筑。这套方案的真实开销见探针 05 的实测。
/// </summary>
public sealed class FactoryLayout
{
    /// <summary>
    /// 4 邻域方向。**顺序是确定性的前提之一**：几何推导里「同一格同时被多条带 / 多台机器看中」时，
    /// 先遍历到的赢 —— 换个顺序就会改变「谁接上了谁」。
    ///
    /// ⚠ **只此一份**：这段字面量以前在本文件里抄了 6 遍（其中 4 遍还在循环条件里
    ///   **每次 new 一个数组**）。各写一份的话，同一种「取邻居」的语义会在不同路径上悄悄不一致。
    /// </summary>
    private static readonly (int Dx, int Dy)[] Neighbours = { (0, -1), (1, 0), (0, 1), (-1, 0) };

    // 用 List<Def?> 表示「槽位」：null = 空洞。List 允许按下标读，这是下标平行存储的前提。
    private readonly List<MachineDef?> _machineSlots = new();
    private readonly List<BeltDef?> _beltSlots = new();
    private readonly List<FluidPipeDef?> _pipeSlots = new();
    private readonly Stack<int> _freeMachineSlots = new();
    private readonly Stack<int> _freeBeltSlots = new();
    private readonly Stack<int> _freePipeSlots = new();

    public int MachineSlotCount => _machineSlots.Count;
    public int BeltSlotCount => _beltSlots.Count;
    public int PipeSlotCount => _pipeSlots.Count;
    public int MachineCount { get; private set; }
    public int BeltCount { get; private set; }
    public int PipeCount { get; private set; }

    /// <summary>管网数（= 所有管道格子的连通分量个数）。</summary>
    public int FluidNetworkCount { get; private set; }

    /// <summary>每个管网有多少格管道（用于算容量）。</summary>
    public int[] FluidCellsPerNetwork { get; private set; } = Array.Empty<int>();

    /// <summary>格 → 管网编号（没有管道就是 -1）。</summary>
    private readonly Dictionary<(int X, int Y), int> _pipeCellToNetwork = new();

    /// <summary>
    /// 带入口格（**含层**）→ 带 id（推导「带→带」连接用；每次 <see cref="Resolve"/> 重建）。
    /// ⚠ 键里必须带层：交错之后同一格可以是两层各一条带的入口。
    /// </summary>
    private readonly Dictionary<(int X, int Y, int Level), int> _beltHeads = new();

    /// <summary>当前有几处「带→带」连接（诊断 / 探针用）。</summary>
    public int BeltLinkCount
    {
        get
        {
            int n = 0;
            for (int b = 0; b < _beltSlots.Count; b++)
                if (_beltSlots[b] is { ToBelt: >= 0 }) n++;
            return n;
        }
    }

    /// <summary>某格属于哪个管网；没有管道返回 -1。</summary>
    public int FluidNetworkAt(GridPos p)
        => _pipeCellToNetwork.TryGetValue((p.X, p.Y), out int n) ? n : -1;

    /// <summary>某一个管网占用的格子数。</summary>
    public int FluidCellsOf(int network)
        => (uint)network < (uint)FluidCellsPerNetwork.Length ? FluidCellsPerNetwork[network] : 0;

    /// <summary>电网数。每台机器归到一个电网；出力按网内机器数算。</summary>
    public int NetworkCount { get; private set; }

    /// <summary>每个电网里有几台机器（用于算发电机出力）。由 <see cref="Resolve"/> 填充。</summary>
    public int[] MachinesPerNetwork { get; private set; } = Array.Empty<int>();

    /// <summary>
    /// **轨道层数**：0 = 低轨、1 = 高轨。两档是刻意的，不是"暂时只有两档"。
    ///
    /// 为什么不做成任意 N 档：占用位图、快照分层与铺瓦的复杂度都随层数**线性**长，
    /// 而「十字交错」这个需求两档就够。真要第三层时，改动点是把
    /// <see cref="OccupancyGrid"/> 里那两张按层的表变成数组、并把渲染顺序那两趟变成循环 ——
    /// 结构上已经为此留好了形状（顺序按层号升序），不必现在就把配置留出来。
    /// </summary>
    public const int MaxLevels = 2;

    /// <summary>把层号夹进合法范围（越界/脏数据当低轨，而不是抛异常 —— 层是外观属性，不该让建造失败）。</summary>
    public static int NormalizeLevel(int level) => level <= 0 ? 0 : Math.Min(level, MaxLevels - 1);

    /// <summary>
    /// 一格的像素边长。与 <c>FactorySim.CellSizePx</c> 必须一致。
    /// </summary>
    public const float CellPx = FactorySim.CellSizePx;

    /// <summary>网格坐标 → 格子中心的世界坐标。</summary>
    public static (float X, float Y) GridToWorld(GridPos p)
        => (p.X * CellPx + CellPx * 0.5f, p.Y * CellPx + CellPx * 0.5f);

    /// <summary>
    /// 世界像素坐标 → 格坐标（<see cref="GridToWorld"/> 的逆）。
    /// 用 floor，所以负坐标也正确落到负格（越界由占用位图挡）。
    ///
    /// ⚠ 收的是两个 float 而不是 Godot 的 <c>Vector2</c>：本工程零 Godot 引用。
    /// ⚠ **只此一份**：交互层与相机都拿它换算，各写一份就会出现「同一像素在不同地方
    ///   落到相邻两格」这类只在边界处现形的问题。
    /// </summary>
    public static GridPos WorldToGrid(float worldX, float worldY) => new(
        (int)MathF.Floor(worldX / CellPx),
        (int)MathF.Floor(worldY / CellPx));

    public FactoryLayout() { }

    public FactoryLayout(IEnumerable<MachineDef> machines, IEnumerable<BeltDef> belts)
    {
        foreach (MachineDef m in machines) _machineSlots.Add(m);
        foreach (BeltDef b in belts) _beltSlots.Add(b);
        MachineCount = _machineSlots.Count;
        BeltCount = _beltSlots.Count;
        Resolve();
    }

    /// <summary>按下标取机器；空洞返回 null。<b>调用方必须能容忍 null</b>（那就是已删除的槽位）。</summary>
    public MachineDef? MachineAt(int id)
        => (uint)id < (uint)_machineSlots.Count ? _machineSlots[id] : null;

    public BeltDef? BeltAt(int id)
        => (uint)id < (uint)_beltSlots.Count ? _beltSlots[id] : null;

    public bool IsMachineAlive(int id) => MachineAt(id) != null;
    public bool IsBeltAlive(int id) => BeltAt(id) != null;

    public FluidPipeDef? PipeAt(int id)
        => (uint)id < (uint)_pipeSlots.Count ? _pipeSlots[id] : null;

    public bool IsPipeAlive(int id) => PipeAt(id) != null;

    /// <summary>加一段管道，返回稳定 id。</summary>
    public int AddPipe(FluidPipeDef def)
    {
        int id;
        if (_freePipeSlots.Count > 0)
        {
            id = _freePipeSlots.Pop();
            _pipeSlots[id] = def;
        }
        else
        {
            id = _pipeSlots.Count;
            _pipeSlots.Add(def);
        }
        PipeCount++;
        return id;
    }

    public bool RemovePipe(int id)
    {
        if (!IsPipeAlive(id)) return false;
        _pipeSlots[id] = null;
        _freePipeSlots.Push(id);
        PipeCount--;
        return true;
    }

    // ── 结构性变更 ──────────────────────────────────────────────────────────

    /// <summary>加一台机器，返回它的稳定 id。复用空洞，否则追加。</summary>
    public int AddMachine(MachineDef def)
    {
        int id;
        if (_freeMachineSlots.Count > 0)
        {
            id = _freeMachineSlots.Pop();
            _machineSlots[id] = def;
        }
        else
        {
            id = _machineSlots.Count;
            _machineSlots.Add(def);
        }
        MachineCount++;
        return id;
    }

    /// <summary>加一段带，返回它的稳定 id。</summary>
    public int AddBelt(BeltDef def)
    {
        int id;
        if (_freeBeltSlots.Count > 0)
        {
            id = _freeBeltSlots.Pop();
            _beltSlots[id] = def;
        }
        else
        {
            id = _beltSlots.Count;
            _beltSlots.Add(def);
        }
        BeltCount++;
        return id;
    }

    /// <summary>删一台机器（置空洞）。**id 不回收给别的机器**，只进空洞池复用。</summary>
    public bool RemoveMachine(int id)
    {
        if (!IsMachineAlive(id)) return false;
        _machineSlots[id] = null;
        _freeMachineSlots.Push(id);
        MachineCount--;
        return true;
    }

    public bool RemoveBelt(int id)
    {
        if (!IsBeltAlive(id)) return false;
        _beltSlots[id] = null;
        _freeBeltSlots.Push(id);
        BeltCount--;

        // ⚠ 绑定必须跟着失效：槽位空洞会被 AddBelt **复用**，不清的话新铺的那条带会
        //   直接"继承"被拆那条的绑定身份 —— 机器会莫名其妙收到一条与它无关的带上的货。
        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;
            def.InBeltBindings = Without(def.InBeltBindings, id);
            def.OutBeltBindings = Without(def.OutBeltBindings, id);
        }
        return true;
    }

    /// <summary>从绑定集合里去掉一个 id（没有就原样返回，避免无意义的分配）。</summary>
    internal static int[] Without(int[] bound, int id)
    {
        if (Array.IndexOf(bound, id) < 0) return bound;
        var kept = new List<int>(bound.Length);
        foreach (int b in bound) if (b != id) kept.Add(b);
        return kept.ToArray();
    }

    /// <summary>某台机器挨着 <paramref name="cell"/> 的那一格（给单格带按阅读顺序排序用）。</summary>
    private static GridPos NeighbourOf(GridPos cell, int machine,
                                       Dictionary<(int X, int Y), int> cellToMachine)
    {
        foreach ((int dx, int dy) in Neighbours)
        {
            var nb = (cell.X + dx, cell.Y + dy);
            if (cellToMachine.TryGetValue(nb, out int m) && m == machine)
                return new GridPos(nb.Item1, nb.Item2);
        }
        return cell;
    }

    /// <summary>
    /// 重算整张图：**带 → 带**的连接（几何、同层优先）+ **机器 ↔ 带**的连接（只认显式绑定）
    /// + 电网 + 管网。**增删带/机器、改绑定之后必须调用**（构造函数里会自动调一次）。
    ///
    /// ⚠ 机器**没有几何兜底**（任何机器都要绑定轨道才能用）：
    ///   没绑定的机器就是 `InBelts/OutBelts` 为空 ⇒ 不工作。
    ///   场景/测试里那些"按几何摆好"的布局，由构造者显式调 <see cref="AutoBindGeometrically"/>。
    /// </summary>
    public void Resolve()
    {
        // 四邻域方向，顺序固定 ⇒ 同一格同时挨着多台机器时挑哪台是确定的

        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;
            def.InBelts = Array.Empty<int>();
            def.OutBelts = Array.Empty<int>();
        }

        // 每段带的**入口格 + 层** → 带 id。按 id 升序插入且只保留第一个 ⇒
        // 「同一格同一层同时是多段带的入口」时（不该发生，但要有确定行为）取 id 最小的那段。
        //
        // ⚠ 键里**必须带层**：交错之后，同一格可以是**两层各一条**带的入口 ——
        //   只用格子做键的话，低轨那条会把高轨那条挤掉（`ContainsKey` 只留第一个），
        //   于是高轨的入口永远接不上上游，货走到头就掉进虚空，而且**画面上看不出任何异常**。
        _beltHeads.Clear();
        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null || belt.Cells.Length == 0) continue;
            var head = (belt.Cells[0].X, belt.Cells[0].Y, FactoryLayout.NormalizeLevel(belt.Level));
            if (!_beltHeads.ContainsKey(head)) _beltHeads[head] = b;
        }

        // ── 第一趟：带 → 带（**同层优先**，没有同层入口时才接另一层）──────────
        //
        // 「变轨」只发生在这里：一段带的**出口格**挨着另一段带的**入口格**时移交一件货。
        // 交错的十字**不是**交接口 —— 两条带只是共用一格、各走各的（见 TransferBetweenBelts：
        // 每段带每 tick 最多交一件给**它的下游**，不会在格子里互相抢）。
        //
        // 为什么同层优先：出口挨着同层的入口时，玩家的意图显然是"接上去"；
        //   只有没有同层入口时才允许跨层（那一次交接等于升降轨一次），
        //   这样至少不会出现「明明挨着一条带，货却掉进虚空」这种静默断链。
        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            // ⚠ 必须和上面建 _beltHeads 的那一趟一样挡住空 Cells ——
            //   否则 Cells[^1] 直接 IndexOutOfRangeException。
            //   （AddBelt 不校验长度，所以空 Cells 是构造得出来的。）
            if (belt == null || belt.Cells.Length == 0) continue;
            var tail = belt.Cells[^1];
            int level = FactoryLayout.NormalizeLevel(belt.Level);
            belt.ToBelt = -1;
            int fallback = -1;
            foreach ((int dx, int dy) in Neighbours)
            {
                var cell = (tail.X + dx, tail.Y + dy);
                if (_beltHeads.TryGetValue((cell.Item1, cell.Item2, level), out int same) && same != b)
                {
                    belt.ToBelt = same;      // 同层：直接接上
                    break;
                }
                foreach (int lv in OtherLevels(level))
                {
                    if (!_beltHeads.TryGetValue((cell.Item1, cell.Item2, lv), out int other)) continue;
                    if (other == b || fallback >= 0) continue;
                    fallback = other;        // 跨层：先记下，等四个方向都没同层入口再说
                }
            }
            if (belt.ToBelt < 0) belt.ToBelt = fallback;
        }

        // ⚠ 消掉**退化二环**：单格带的入口与出口是**同一格**，于是「上一段的出口」
        //   同时也挨着「本段的入口」，两段会互相指向（A→B 且 B→A）—— 后果是货被往回送：
        //   实测一条 6 段单格带的直线链，最后一段把货交回倒数第二段，出货机永远拿不到。
        //   至少有一段是单格时删掉「大号 → 小号」那一侧（保留小号 → 大号，与「先画的在上游」一致）；
        //   两段都是多格时那是**真环**（环形带），保留不动。
        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null || belt.ToBelt < 0 || belt.ToBelt > b) continue;
            BeltDef? next = _beltSlots[belt.ToBelt];
            if (next == null || next.ToBelt != b) continue;                 // 不是二环
            if (belt.Cells.Length > 1 && next.Cells.Length > 1) continue;    // 真环，保留
            belt.ToBelt = -1;
        }

        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt != null) belt.FromBelt = -1;
        }
        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null || belt.ToBelt < 0) continue;
            BeltDef? next = _beltSlots[belt.ToBelt];
            if (next != null && next.FromBelt < 0) next.FromBelt = b;
        }

        // ── 第二趟：机器 ↔ 带（**只认显式绑定**，没有几何兜底）────────────────────
        //
        // **任何机器都要绑定轨道才能使用**（看机器类型决定是否要输入/输出）。
        // 所以这里不做任何"顺便连上"的推导：绑定表是**唯一的**连接来源，
        // 没绑定的机器就是 InBelts/OutBelts 为空 ⇒ 不工作（生产者产不出、加工机干等、出货机没货）。
        // 需要哪些角色由 <see cref="MachineWiring"/> 定义（水塔/泵不需要任何轨道角色）。
        //
        // 同一条带被两台机器绑成同一角色时**先到先得**（按机器 id 升序，结果确定）：
        // UI 侧会先拒掉这种绑定（见 FactorySim.BindMachineTrack 的校验），
        // 这里只是保证「即使有人绕过 UI 直接摆布局」也有一个确定的结果，而不是看字典顺序。
        var claimedTo = new int[_beltSlots.Count];
        var claimedFrom = new int[_beltSlots.Count];
        Array.Fill(claimedTo, -1);
        Array.Fill(claimedFrom, -1);

        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;

            foreach (int belt in def.InBeltBindings)
                if (IsBeltAlive(belt) && claimedTo[belt] < 0) claimedTo[belt] = m;
            foreach (int belt in def.OutBeltBindings)
                if (IsBeltAlive(belt) && claimedFrom[belt] < 0) claimedFrom[belt] = m;
        }

        // 落绑：绑定关系写在**带上**（inLists/outLists 是从带反查出来的，
        // 所以这里写对了，机器的 InBelts/OutBelts 就自动对了 —— 一份真相）。
        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null) continue;
            belt.ToMachine = claimedTo[b];
            belt.FromMachine = claimedFrom[b];
        }

        // 按带 id 升序把每台机器的输入/输出带收集起来（顺序确定 ⇒ 分流/合流的取货顺序确定）
        var inLists = new List<int>[_machineSlots.Count];
        var outLists = new List<int>[_machineSlots.Count];
        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null) continue;

            int from = belt.FromMachine;
            if (IsMachineAlive(from))
            {
                (outLists[from] ??= new List<int>()).Add(b);
            }
            int to = belt.ToMachine;
            if (IsMachineAlive(to))
            {
                (inLists[to] ??= new List<int>()).Add(b);
            }
        }

        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;
            def.InBelts = inLists[m]?.ToArray() ?? Array.Empty<int>();
            def.OutBelts = outLists[m]?.ToArray() ?? Array.Empty<int>();
        }

        int nets = 0;
        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;
            // NetworkId = -1 是**文档化的哨兵**（「未分配 / 新拉一条电网」，见 MachineDef）。
            // 它不属于任何电网，所以既不占号、也不进下面的计数。
            if (def.NetworkId < -1)
                throw new InvalidOperationException(
                    $"机器 #{m} 的 NetworkId = {def.NetworkId} 非法（合法值：-1 = 未分配，或 >= 0）。");
            if (def.NetworkId + 1 > nets) nets = def.NetworkId + 1;
        }
        NetworkCount = nets;

        MachinesPerNetwork = new int[nets];
        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;
            // ⚠ 必须挡住 -1：这一句原来是无保护的 `MachinesPerNetwork[-1]++`，
            //   而 -1 正是上面刚认定的合法哨兵 ⇒ 直接把整场模拟炸掉。
            if (def.NetworkId >= 0) MachinesPerNetwork[def.NetworkId]++;
        }

        ResolveFluidNetworks();
    }

    /// <summary>
    /// 一台机器**够得着的带**（按带 id 升序，已去重）—— 这就是「点机器之后能选哪几条轨道」的候选表。
    ///
    /// ⚠ 判据与 <see cref="Resolve"/> 的几何邻接**必须同口径**，否则会出现
    ///   「UI 里选得到、几何却接不上」这种自相矛盾的状态：
    ///     · 半径 1：网格四邻域内碰到机器占地任意一格；
    ///     · 半径 2：**只有长臂机械臂**够得到（与 Resolve 里的 <c>far</c> 那一支同一规则）。
    ///
    /// 已有的绑定也会被列进去（哪怕它已经不挨着机器了）—— 否则玩家没法解除一个
    /// 「带被挪走/改过」之后留下的旧绑定。
    /// </summary>
    public List<int> ReachableBelts(int machineId)
    {
        var result = new List<int>();
        MachineDef? m = MachineAt(machineId);
        if (m == null) return result;

        var foot = new HashSet<(int X, int Y)>();
        foreach (GridPos p in m.Footprint()) foot.Add((p.X, p.Y));

        bool inserter = m.Kind == MachineKind.Inserter;
        var seen = new HashSet<int>();

        void Add(int belt)
        {
            if (belt >= 0 && IsBeltAlive(belt) && seen.Add(belt)) result.Add(belt);
        }

        foreach (int b in m.InBeltBindings) Add(b);
        foreach (int b in m.OutBeltBindings) Add(b);

        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null || seen.Contains(b)) continue;
            bool hit = false;
            foreach (GridPos c in belt.Cells)
            {
                foreach ((int dx, int dy) in Neighbours)
                {
                    if (foot.Contains((c.X + dx, c.Y + dy)))
                    {
                        hit = true;
                        break;
                    }
                    if (inserter && foot.Contains((c.X + dx * 2, c.Y + dy * 2)))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit) break;
            }
            if (hit) Add(b);
        }

        result.Sort();
        return result;
    }

    /// <summary>
    /// **按几何把「还没接的角色」自动接上** —— 给**场景构造者与测试**用的一次性便利，
    /// **不是模拟的规则**（模拟只认显式绑定，见 <see cref="Resolve"/>）。
    ///
    /// 它做的事就是按几何推导一遍（多格带看首/尾两格、单格带按阅读顺序、
    /// 长臂机械臂够两格），并**把结果写成绑定**：入口格挨着哪台机器 ⇒ 那台机器的输入 = 这条带，
    /// 出口格挨着哪台机器 ⇒ 那台机器的输出 = 这条带。已经绑好的角色**不动**。
    ///
    /// 为什么保留它：演示布局（`BuildDemo`）、压力场景、以及一批以"按几何摆好"为前提的探针
    /// 都需要一个"照旧连上"的入口；把它做成**显式一次调用**，游戏里的建造路径就完全碰不到它。
    /// </summary>
    public void AutoBindGeometrically()
    {

        // 格 → 机器 id（把多格建筑的整个占地都铺进表里）
        var cellToMachine = new Dictionary<(int X, int Y), int>();
        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;
            foreach (GridPos p in def.Footprint()) cellToMachine[(p.X, p.Y)] = m;
        }

        int Adjacent(GridPos cell)
        {
            // 半径 1：任何机器
            foreach ((int dx, int dy) in Neighbours)
                if (cellToMachine.TryGetValue((cell.X + dx, cell.Y + dy), out int a)) return a;

            // 半径 2：**只有长臂机械臂够得到**（它打破「机器必须直接贴带」）。
            // ⚠ 必须在半径 1 全部查完之后再查，否则「旁边明明有台机器」会被远方的机械臂抢走。
            foreach ((int dx, int dy) in Neighbours)
            {
                var far = (cell.X + dx * 2, cell.Y + dy * 2);
                if (!cellToMachine.TryGetValue(far, out int b)) continue;
                if (_machineSlots[b]!.Kind == MachineKind.Inserter) return b;
            }
            return -1;
        }

        void BindInput(int machine, int belt)
        {
            MachineDef d = _machineSlots[machine]!;
            if (d.IsBoundInput(belt)) return;
            d.InBeltBindings = Sorted(d.InBeltBindings, belt);
        }

        void BindOutput(int machine, int belt)
        {
            MachineDef d = _machineSlots[machine]!;
            if (d.IsBoundOutput(belt)) return;
            d.OutBeltBindings = Sorted(d.OutBeltBindings, belt);
        }

        for (int b = 0; b < _beltSlots.Count; b++)
        {
            BeltDef? belt = _beltSlots[b];
            if (belt == null || belt.Cells.Length == 0) continue;

            int from, to;
            if (belt.Cells.Length > 1)
            {
                from = Adjacent(belt.Cells[0]);
                to = Adjacent(belt.Cells[^1]);
            }
            else
            {
                // ⚠ 单格带：入口与出口是**同一格**，一次查找会把同一台机器同时当成上下游
                //   —— 那会让「放一格带连接两台机器」整条线不动（机器把带既当输入又当输出）。
                //   规则（全部确定，不依赖字典顺序）：
                //     · 邻了 ≥2 台机器 ⇒ 按**阅读顺序**（先上后下、先左后右）：靠前的是上游；
                //     · 邻了 1 台 ⇒ 看它自己往哪边走（有下游带 ⇒ 它是上游；只有上游带 ⇒ 它是下游；
                //       都没有 ⇒ 当作机器喂它）。
                GridPos cell = belt.Cells[0];
                var touching = new List<int>();
                foreach ((int dx, int dy) in Neighbours)
                {
                    if (!cellToMachine.TryGetValue((cell.X + dx, cell.Y + dy), out int m)) continue;
                    if (!touching.Contains(m)) touching.Add(m);
                }

                if (touching.Count == 0)
                {
                    from = to = -1;
                }
                else if (touching.Count == 1)
                {
                    int only = touching[0];
                    if (belt.ToBelt >= 0) { from = only; to = -1; }
                    else if (belt.FromBelt >= 0) { from = -1; to = only; }
                    else { from = only; to = -1; }
                }
                else
                {
                    touching.Sort((p, q) =>
                    {
                        var pp = NeighbourOf(cell, p, cellToMachine);
                        var qq = NeighbourOf(cell, q, cellToMachine);
                        int c = pp.Y.CompareTo(qq.Y);
                        return c != 0 ? c : pp.X.CompareTo(qq.X);
                    });
                    from = touching[0];
                    to = touching[1];
                }
            }

            if (from >= 0 && NeedsAny(from, out_: true)) BindOutput(from, b);
            if (to >= 0 && NeedsAny(to, out_: false)) BindInput(to, b);
        }

        bool NeedsAny(int machine, bool out_)
        {
            MachineKind k = _machineSlots[machine]!.Kind;
            return out_ ? MachineWiring.NeedsOutput(k) : MachineWiring.NeedsInput(k);
        }
    }

    /// <summary>把一个 id 并进绑定集合（保持升序去重 —— 顺序确定是确定性的前提）。</summary>
    private static int[] Sorted(int[] bound, int id)
    {
        var next = new List<int>(bound) { id };
        next.Sort();
        return next.ToArray();
    }

    /// <summary>除 <paramref name="level"/> 之外的所有层（层数上限见 <see cref="MaxLevels"/>）。</summary>
    private static IEnumerable<int> OtherLevels(int level)
    {
        for (int lv = 0; lv < MaxLevels; lv++)
            if (lv != FactoryLayout.NormalizeLevel(level)) yield return lv;
    }

    /// <summary>
    /// 算管网：**所有管道格子的四邻域连通分量**。
    ///
    /// 为什么要连通分量而不是「一段管道 = 一个管网」：
    /// 玩家会分好几次铺管道，只要它们边相邻就应当合成同一个管网 ——
    /// 否则「往中间再接一段管子」会把一个管网拆成两个互不相通的，行为完全反直觉。
    ///
    /// 确定性：起点按格子坐标排序、用队列 BFS，邻居按固定顺序访问。
    /// </summary>
    private void ResolveFluidNetworks()
    {
        _pipeCellToNetwork.Clear();

        var cells = new HashSet<(int X, int Y)>();
        for (int i = 0; i < _pipeSlots.Count; i++)
        {
            FluidPipeDef? def = _pipeSlots[i];
            if (def == null) continue;
            foreach (GridPos p in def.Cells) cells.Add((p.X, p.Y));
        }

        var seeds = new List<(int X, int Y)>(cells);
        seeds.Sort();

        var queue = new Queue<(int X, int Y)>();
        var counts = new List<int>();
        int network = 0;

        foreach ((int X, int Y) start in seeds)
        {
            if (_pipeCellToNetwork.ContainsKey(start)) continue;

            int count = 0;
            queue.Enqueue(start);
            _pipeCellToNetwork[start] = network;

            while (queue.Count > 0)
            {
                (int x, int y) = queue.Dequeue();
                count++;

                foreach ((int dx, int dy) in Neighbours)
                {
                    var nb = (x + dx, y + dy);
                    if (!cells.Contains(nb) || _pipeCellToNetwork.ContainsKey(nb)) continue;
                    _pipeCellToNetwork[nb] = network;
                    queue.Enqueue(nb);
                }
            }

            counts.Add(count);
            network++;
        }

        FluidNetworkCount = network;
        FluidCellsPerNetwork = counts.ToArray();

        // 机器贴到哪个管网：取占地四邻域里**编号最小**的那个（确定性），没接上就是 -1。
        // 泵需要**两个**不同的管网（它就是跨网搬运用的），所以这里把四邻域里所有不同的
        // 管网编号都收集起来、升序排列：第一个给 FluidNetwork，第二个给 FluidNetworkB。
        for (int m = 0; m < _machineSlots.Count; m++)
        {
            MachineDef? def = _machineSlots[m];
            if (def == null) continue;

            var touching = new List<int>();
            foreach (GridPos cell in def.Footprint())
            {
                foreach ((int dx, int dy) in Neighbours)
                {
                    if (!_pipeCellToNetwork.TryGetValue((cell.X + dx, cell.Y + dy), out int n)) continue;
                    if (!touching.Contains(n)) touching.Add(n);
                }
            }
            touching.Sort();

            def.FluidNetwork = touching.Count > 0 ? touching[0] : -1;
            def.FluidNetworkB = touching.Count > 1 ? touching[1] : -1;
        }
    }

    /// <summary>
    /// **物品陈列区**：为每种物品铺一条**短带**（默认 6 格），排成 <paramref name="columns"/> 列的网格。
    ///
    /// 用途是「把物品摆到网格上给玩家看」——物品只存在于带上，所以「陈列物品」在实现上
    /// 就是「铺一条带、再往里灌那一种物品」（见 <c>FactorySim.PrimeBelts</c>）。
    /// 短带灌满后会**压死不动**，正好当静态展品（不是 bug，是刻意的）。
    ///
    /// ⚠ 为什么要**分列**而不是「一行一条」：一行一条时 7 种物品要占 7 行（间距 2 ⇒ 13 行），
    ///   而演示布局的链行**每 5 行一条**（y = 2,7,12,…），13 行的窗口无论从哪一行起都必然
    ///   压到某条链上。分列后只占 2 行，正好塞进两条链之间那段**完全空**的 4 行里。
    ///   这是被「重叠」逼出来的形状，不是审美选择。
    ///
    /// 为什么要做成布局的一部分、而不是发建造命令：<c>PrimeBelts</c> 必须在
    /// **模拟线程启动之前**调用（它直接改带的内容），所以陈列带得在构造 <c>FactorySim</c>
    /// 之前就进入布局。返回第一条陈列带的 id，调用方据此只灌这一段（见
    /// <c>PrimeBelts</c> 的 <c>onlyFromBeltIndex</c>）。
    /// </summary>
    public int AddItemGallery(int originX, int originY, int count, int cells = 6,
                              int spacing = 2, int columns = 4)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        // ⚠ 返回的必须是**第一条陈列带的 id**，所以只能取 AddBelt 的返回值 ——
        //   不能拿 `BeltCount`（那是**存活条数**）。带 id 是从**空闲栈**里弹的，
        //   布局里只要出现过空洞，两者就不相等 ⇒ 调用方（PrimeBelts 的 onlyFromBeltIndex）
        //   会从错误的位置开始灌，静默漏掉陈列区的第一条带。
        int first = -1;
        for (int i = 0; i < count; i++)
        {
            int ox = originX + (i % columns) * GalleryColumnPitch(cells);
            int oy = originY + (i / columns) * spacing;
            var cs = new GridPos[cells];
            for (int k = 0; k < cells; k++) cs[k] = new GridPos(ox + k, oy);
            int id = AddBelt(new BeltDef { Cells = cs });
            if (i == 0) first = id;
        }
        return first;
    }

    /// <summary>
    /// 陈列区**列间距**：带长 + 3 格。留 3 格是因为名字标签画在带的左端、水平居中，
    /// 约 2 格宽 —— 贴太近两条带的标签会连成一串。
    /// ⚠ 布局与「找空地」必须用同一个值，所以它是这里的一个函数，不许两处各写一个数。
    /// </summary>
    public static int GalleryColumnPitch(int cells) => cells + 3;

    /// <summary>
    /// **交错高低轨展示区**（用户要求：主场景里放一段复杂的、交错的高低轨道做示例）。
    ///
    /// 摆出来的东西（<paramref name="ox"/>,<paramref name="oy"/> 是左上角，占 3 行 × 20 列）：
    ///
    /// <code>
    ///   y+0:              ┌ 高轨 L1（竖，向下）起点
    ///   y+1:      熔炉 F ─┤      S 出货机
    ///   y+2:  矿机 A ──低轨 L0（横，向东）──┼──────→（到 x+18）
    ///                                        └ 高轨 L1 的尾格与低轨 L0 的某一格**是同一格**（交错）
    /// </code>
    ///
    /// 这一段同时演示三件事，而且**每一件都是被需求逼出来的、不是摆着好看**：
    ///   ① **交错**：L0（低）与 L1（高）在 (x+8, y+2) 共用同一格 —— 没有层就摆不出来；
    ///   ② **遮挡**：L0 上流过的物品到那一格会被 L1 的瓦片挡住（渲染顺序按层，见 FactorySim）；
    ///   ③ **显式绑定**：三处连接**几何全都接不上** —— 熔炉贴在低轨的**中段**、
    ///      高轨的**中段**贴着熔炉和出货机，而几何只看带的首/尾两格。
    ///      所以这个展示区不绑定就完全不工作 —— 它正是「为什么需要绑定」的活证据。
    /// </summary>
    public readonly record struct TrackInterleaveDemo(
        GridPos Mine, GridPos Furnace, GridPos Shipper, int LowBelt, int HighBelt, GridPos Crossing);

    /// <summary>把交错高低轨展示区加进布局（见 <see cref="TrackInterleaveDemo"/> 的图）。</summary>
    public static TrackInterleaveDemo AddTrackInterleaveDemo(FactoryLayout layout, ContentDb content,
                                                              int ox, int oy)
    {
        BuildableDef mineDef = content.Buildable("iron-mine")
            ?? throw new InvalidOperationException("内容表里没有 iron-mine —— 展示区需要它");
        BuildableDef furnaceDef = content.Buildable("iron-furnace")
            ?? throw new InvalidOperationException("内容表里没有 iron-furnace —— 展示区需要它");
        BuildableDef shipperDef = content.Buildable("shipper")
            ?? throw new InvalidOperationException("内容表里没有 shipper —— 展示区需要它");

        // 自成一个电网：不动既有产线的电力数字（展示区是独立的例子）
        int net = layout.NetworkCount;

        var minePos = new GridPos(ox, oy + 2);
        var furnacePos = new GridPos(ox + 7, oy + 1);
        var shipperPos = new GridPos(ox + 9, oy + 1);

        int mine = layout.AddMachine(mineDef.ToMachineDef(content, minePos, 0, net, 1));
        int furnace = layout.AddMachine(furnaceDef.ToMachineDef(content, furnacePos, 0, net, 0));
        int shipper = layout.AddMachine(shipperDef.ToMachineDef(content, shipperPos, 0, net, 0));

        // 低轨（横，向东）：主路径。经过交叉格 (ox+8, oy+2)。
        var low = new GridPos[18];
        for (int k = 0; k < low.Length; k++) low[k] = new GridPos(ox + 1 + k, oy + 2);
        int lowBelt = layout.AddBelt(new BeltDef { Cells = low, Level = 0 });

        // 高轨（竖，向南）：从熔炉那一行上方下来，尾格**正落在低轨的一格上** ⇒ 这就是交错。
        var high = new GridPos[3];
        for (int k = 0; k < high.Length; k++) high[k] = new GridPos(ox + 8, oy + k);
        int highBelt = layout.AddBelt(new BeltDef { Cells = high, Level = 1 });

        // 显式绑定：这三条关系几何一条都推不出来（全都靠中段擦边）
        layout.MachineAt(furnace)!.InBeltBindings = new[] { lowBelt };      // 熔炉吃低轨的货
        layout.MachineAt(furnace)!.OutBeltBindings = new[] { highBelt };    // 熔炉把成品推到高轨上
        layout.MachineAt(shipper)!.InBeltBindings = new[] { highBelt };     // 出货机从高轨收货

        layout.Resolve();
        return new TrackInterleaveDemo(minePos, furnacePos, shipperPos, lowBelt, highBelt,
                                       new GridPos(ox + 8, oy + 2));
    }

    /// <summary>
    /// 验证用布局：一条 **L 形带**（先向东、再向南），用来在游戏里核对拐角铺瓦是否正确。
    /// 分类器算出的掩码表和它的目视读法在拐角上不一定一致，所以必须有这么一个「看得见」的验证。
    /// 用 <c>FORGEFLOW_DEMO_CORNER=1</c> 启用。
    /// </summary>
    public static FactoryLayout BuildCornerDemo(int workTicks)
    {
        var layout = new FactoryLayout();

        layout.AddMachine(new MachineDef
        {
            Kind = MachineKind.Producer,
            Pos = new GridPos(1, 2),
            WorkTicks = workTicks,
            NetworkId = 0,
            InitialItemType = 1,
        });

        var cells = new List<GridPos>();
        for (int x = 2; x <= 8; x++) cells.Add(new GridPos(x, 2));   // 向东
        for (int y = 3; y <= 6; y++) cells.Add(new GridPos(8, y));   // 向南

        int belt = layout.AddBelt(new BeltDef
        {
            Cells = cells.ToArray(),
        });
        _ = belt;

        layout.AddMachine(new MachineDef
        {
            Kind = MachineKind.Shipper,
            Pos = new GridPos(8, 7),
            WorkTicks = workTicks,
            NetworkId = 0,
        });

        layout.Resolve();
        return layout;
    }

    /// <summary>
    /// 演示布局：chains 条链，每条链 stages 台机器，机器之间夹一段 <paramref name="cellsPerBelt"/> 格的直线带。
    ///
    /// 机器 id 按 **链优先、级次之** 排；带 id 按 **链优先、段次之** 排 ——
    /// 这个顺序必须稳定，因为它决定遍历顺序，也就决定确定性。
    /// （稳定 id 方案下，这里用的是追加，所以 id 天然是 0,1,2,…）
    /// </summary>
    public static FactoryLayout BuildDemo(
        int chains, int stages, int cellsPerBelt,
        int workTicks, int producerWorkTicks, int typeCount)
    {
        if (chains < 1) throw new ArgumentOutOfRangeException(nameof(chains));
        if (stages < 2) throw new ArgumentOutOfRangeException(nameof(stages), "至少 2 级（生产机 + 出货机）");
        if (cellsPerBelt < 1) throw new ArgumentOutOfRangeException(nameof(cellsPerBelt));

        var layout = new FactoryLayout();
        int beltsPerChain = stages - 1;
        int stagePitch = 1 + cellsPerBelt;     // 一台机器占 1 格，后面跟 cellsPerBelt 格带
        const int rowPitch = 5;                // 链与链之间空几格，纯粹为了好看

        for (int c = 0; c < chains; c++)
        {
            int gy = 2 + c * rowPitch;
            for (int s = 0; s < stages; s++)
            {
                MachineKind kind = s == 0 ? MachineKind.Producer
                                 : s == stages - 1 ? MachineKind.Shipper
                                 : MachineKind.Processor;

                layout.AddMachine(new MachineDef
                {
                    Kind = kind,
                    Pos = new GridPos(1 + s * stagePitch, gy),
                    WorkTicks = s == 0 ? producerWorkTicks : workTicks,
                    NetworkId = c,
                    InitialItemType = (ushort)(1 + c % typeCount),
                });
            }

            for (int l = 0; l < beltsPerChain; l++)
            {
                int startX = 1 + l * stagePitch + 1;
                var cells = new GridPos[cellsPerBelt];
                for (int k = 0; k < cellsPerBelt; k++)
                    cells[k] = new GridPos(startX + k, gy);

                layout.AddBelt(new BeltDef
                {
                    Cells = cells,
                });
            }
        }

        layout.Resolve();
        // ⚠ 演示布局是**按几何摆好的**（"机器后面跟一段带"），而机器现在只认显式绑定
        //   （见 Resolve 的说明）—— 所以这里显式把几何配对写进绑定。游戏里的建造路径不会走这一句。
        layout.AutoBindGeometrically();
        layout.Resolve();
        return layout;
    }
}
