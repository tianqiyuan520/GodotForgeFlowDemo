using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

/// <summary>
/// 工厂模拟：驱动带 + 机器的模拟主干。
///
/// **拓扑来自 <see cref="FactoryLayout"/>（数据），不是构造函数里生成的**，而且是**可变**的：
/// 玩家要能随时增删工厂的形状。
///
/// ── 下标平行存储 + 稳定 id + 空洞 ──────────────────────────────────────────
/// 机器的状态（进度、携带物、在不在活跃位图里）与带的物品全部**按 id 下标平行存储**。
/// 布局用「稳定 id + 空洞」（见 <see cref="FactoryLayout"/>），所以删除一台机器
/// **不会让别的机器的状态错位** —— <see cref="SyncTopology"/> 只补齐新增、作废已删，
/// 存活机器的加工进度原样保留。这正是「玩家边建边跑」必须的语义。
///
/// 每 tick 遍历仍**按 id 升序**（含空洞），这是确定性的前提，也是休眠模式与全量模式等价的前提。
///
/// ── 四条设计决定 ────────────────────────────────────────────────────────────
///
/// **① 活跃位图 + id 序遍历**（而不是紧凑活跃数组）
///    紧凑数组要维持 id 序就得做有序插入（易错），而必须保持 id 序的原因是：
///    机器之间通过带间接耦合，遍历顺序不同会让「谁先读写同一条带」不一致 ⇒ 结果发散。
///    位图扫描 O(机器数) 但只是 ~1ns/台，相对 16.67ms 预算可忽略；
///    真正省下的是**每台睡眠机器的状态机工作**。
///    ⇒ 实测在饱和产线里几乎无收益（0.84×），只有饥饿形态 1.22× ——
///      **不作为主干优化**，但保留实现与等价比对探针。
///
/// **② 唤醒边只有两条，且判定必须紧跟带的推进**
///    W1：某条带上有物品到达出口（gap[0] == 0）→ 唤醒该带的**消费者**（若它在等料）
///    W2：某条带的入口有空间（TailGap ≥ MinGapSub）→ 唤醒该带的**生产者**（若它被堵）
///
/// **③ 取料由消费者机器负责，带自己不做取出**
///
/// **④ 电力是「增量维护 + 每 tick 求解」，不是每 tick 全量聚合**
///    机器进入/离开 Working 时 O(1) 更新所属电网的需求；求解本身只遍历电网数。
///    全量聚合是 O(机器数)/tick，可作为 <see cref="PowerIncremental"/> = false 的对照。
/// </summary>
/// <remarks>
/// 这个类很大，但它是 <c>partial</c> 的：**按主题拆在几个文件里，找东西时按文件名找** ——
/// <list type="bullet">
///   <item><description><c>FactorySim.cs</c>（本文件）—— 字段、构造、<see cref="Tick"/> 主循环、只读查询接口</description></item>
///   <item><description><c>FactorySim.Machine.cs</c> —— 一台机器一个 tick 做什么（状态机 / 配方 / 仓储箱）</description></item>
///   <item><description><c>FactorySim.Build.cs</c> —— 建造命令、撤销重做、接线</description></item>
///   <item><description><c>FactorySim.Blueprint.cs</c> —— 蓝图复制粘贴</description></item>
///   <item><description><c>FactorySim.Snapshot.cs</c> —— 发给渲染线程的快照与视口剔除</description></item>
///   <item><description><c>FactorySim.Fluid.cs</c> —— 流体管网</description></item>
///   <item><description><c>FactorySim.Power.cs</c> —— 电力求解</description></item>
///   <item><description><c>FactorySim.Atlas.cs</c> —— 一格的图形长什么样（图集映射 / 自动铺瓦 / 精灵表）</description></item>
/// </list>
/// 拆分只是**整行搬移**，没有任何逻辑改动；各文件共享同一份字段，访问权限与合并时完全一样。
/// </remarks>
public sealed partial class FactorySim : ISimSource
{
    public const int SubdivPerCell = 16;
    public const int MinGapSub = SubdivPerCell;

    /// <summary>
    /// 一个格在世界里多少像素。
    ///
    /// **必须等于图集瓦片的原生尺寸。** 这套 sierrassets 美术是 **32×32** 瓦片，
    /// 所以「一格 = 一瓦片 = 32px」。机器与物品也各自以「瓦片」为单位，
    /// 比例天然统一；想看得更大就缩放相机（见 Scenes/Main.tscn 的 Camera2D）。
    /// </summary>
    public const float CellSizePx = 32f;

    private const float PxPerSub = CellSizePx / SubdivPerCell;

    /// <summary>供电率用千分比整数表示，保证确定性（避免浮点）。</summary>
    public const int SatFull = 1000;

    // ── 拓扑（数据，可变） ──────────────────────────────────────────────────
    private readonly FactoryLayout _layout;
    private int[] _networkOf = Array.Empty<int>();
    private int _netCount;

    // ── 下标平行存储（下标 = 稳定 id；空洞用 *_Alive 标记） ─────────────────
    private int _machineSlots;
    private int _beltSlots;
    private bool[] _machineAlive = Array.Empty<bool>();
    private bool[] _beltAlive = Array.Empty<bool>();

    /// <summary>
    /// 槽位上一轮同步时放着的是**哪一个 def 实例**（机器 / 带各一份）。
    ///
    /// 为什么不能只看 <c>*_Alive</c>：建造命令是**整批应用完才同步一次**拓扑的
    /// （见 <see cref="DrainBuildQueue"/>），所以「同一批里先拆、再在**同一格**放新东西」
    /// 会让 <c>_machineAlive[m]</c> 从头到尾都是 <c>true</c> —— 新机器于是继承了旧机器的
    /// 进度 / 缓冲 / 携带物（旧配方攒下的料新配方永远吃不下 ⇒ 静默卡死），
    /// 带则保留旧的 <see cref="GapLine"/>（长度与容量还是旧的，件数可以超出新带的格数）。
    /// 比 **def 实例身份**才认得出「这格换了个东西」；槽位空掉时清成 null，
    /// 于是「拆掉 → 之后撤销回来」仍按**新东西**初始化。
    /// </summary>
    private MachineDef?[] _defOfMachine = Array.Empty<MachineDef?>();
    private BeltDef?[] _defOfBelt = Array.Empty<BeltDef?>();
    private GapLine?[] _belts = Array.Empty<GapLine?>();
    private ushort[]?[] _beltCellSprites = Array.Empty<ushort[]?>();   // 逐格自动铺瓦结果
    /// <summary>逐格朝向（0 或 π）。只有「往西 / 往北的直线段」是 π —— 箭头必须对着流向。</summary>
    private float[]?[] _beltCellRot = Array.Empty<float[]?>();
    private int[][] _inBelts = Array.Empty<int[]>();
    private int[][] _outBelts = Array.Empty<int[]>();

    /// <summary>
    /// 分流器的轮转游标。**是模拟状态的一部分**（进 StateHash）——
    /// 它决定下一个物品走哪条输出带，所以等价比对必须把它算进去，否则
    /// 「两边游标不同但别的都一样」会被误判为等价。
    /// 一进一出的机器上它恒为 0，不改变任何行为。
    /// </summary>
    private int[] _splitCursor = Array.Empty<int>();

    /// <summary>带 id → 下游那段带的 id（-1 = 没有）。结构变更时重建。</summary>
    private int[] _beltDownstream = Array.Empty<int>();

    /// <summary>逐带包围盒（世界像素），剔除用。随结构变更重建，见 <see cref="RebuildBeltTiles"/>。</summary>
    private WorldRect[] _beltBounds = Array.Empty<WorldRect>();

    /// <summary>「带→带」移交的意向缓冲（避免每 tick 分配）。</summary>
    private int[] _transferFrom = Array.Empty<int>();

    /// <summary>
    /// 「带→带」移交累计次数。**物料守恒等式要用它**：
    /// 每次移交会让上游带的「取出」与下游带的「插入」各 +1，但物品并没有被创造 ——
    /// 所以「机器送出的件数」= 带累计插入 − 移交次数。
    /// </summary>
    public long BeltTransferCount { get; private set; }

    // ── 配方与机器输入缓冲 ──────────────────────────────────────────────────
    //
    // ⚠ 缓冲是**平铺的定长数组**：机器 m 的槽位 = [m * InvSlots, m * InvSlots + InvSlots)。
    //
    // 为什么不做「变长 + 前缀和」：那样每次增删机器都会让后面所有机器的缓冲整体平移，
    // 就必须写一段「搬迁存活机器内容」的迁移代码 —— 而那段代码一旦写错，症状是
    // 玩家放一台机器、别的机器里的料偷偷变了，极难查。
    // 定长 24 槽 = 48 B/机器，10,000 台也才 480 KB，**不值得为它引入一个易错的结构**。
    private int[] _recipeOf = Array.Empty<int>();
    private int[] _storageSlots = Array.Empty<int>();
    private int[] _facing = Array.Empty<int>();
    private ushort[] _inv = Array.Empty<ushort>();
    private int[] _outPushed = Array.Empty<int>();
    private readonly bool[] _recipeScratch = new bool[InvSlots];

    /// <summary>
    /// 配方机**开工过几次**（= 开工时就把输入扣掉了）。
    ///
    /// ⚠ **故意不进 <see cref="StateHash"/>**：它是一个只增的统计量，不参与任何后续决策，
    ///   所以「指纹相同但计数不同」在语义上是同一个状态。把它塞进指纹只会让
    ///   「结构变更后与重建一份是否等价」这类比对因为历史路径不同而误报。
    ///
    /// ⚠ 与 <see cref="RecipeCraftsFinished"/> **必须分开**：输入在开工时扣、产出在完工时推，
    ///   两者之间有一件「在制品」。守恒等式要用**完成**数算产出、用**开工**数算消耗，
    ///   混用会得到「每台在制机器各差一件」的假失衡（这个坑是本探针抓出来的）。
    /// </summary>
    private int[] _craftsStarted = Array.Empty<int>();

    /// <summary>配方机**完工过几次**（产出已全部推进输出带）。</summary>
    private int[] _craftsFinished = Array.Empty<int>();

    /// <summary>
    /// 每台机器的输入缓冲槽数。**配方机只用前几个**，仓储箱用满。
    /// 定长是为了避免「增删机器导致缓冲整体平移」（见上面的说明）。
    /// </summary>
    public const int InvSlots = 24;

    private readonly ContentDb _content;

    private readonly int _typeCount;
    private readonly int _demandPerMachine;

    // ── 机器状态 ────────────────────────────────────────────────────────────
    private byte[] _state = Array.Empty<byte>();
    private int[] _progress = Array.Empty<int>();
    private int[] _work = Array.Empty<int>();
    private ushort[] _carried = Array.Empty<ushort>();
    private bool[] _active = Array.Empty<bool>();

    /// <summary>
    /// **这台机器接够线了吗**（见 <see cref="MachineDef.IsWired"/> / <see cref="MachineWiring"/>）。
    /// 在 <see cref="SyncTopology"/> 里更新，<see cref="TickMachine"/> 每 tick 只读它。
    /// 没接够 ⇒ 完全不工作。
    /// </summary>
    private bool[] _wired = Array.Empty<bool>();

    // ── 电力 ────────────────────────────────────────────────────────────────
    private int[] _netGen = Array.Empty<int>();
    private int[] _networkDemand = Array.Empty<int>();
    private int[] _networkSat = Array.Empty<int>();

    // ── 开关（供探针 / 诊断） ────────────────────────────────────────────────
    /// <summary>true = 用活跃位图休眠；false = 每 tick 全量遍历（对照用）。</summary>
    public bool UseActiveSet { get; set; } = true;

    /// <summary>false = 关掉 W1/W2 唤醒边（**只用于反例测试**，会静默卡死）。</summary>
    public bool WakeEdgesEnabled { get; set; } = true;

    /// <summary>false = 不输出带面底层（大场景压测时可关掉，省掉 O(带数 × 格数) 的实例）。</summary>
    public bool RenderBeltBases { get; set; } = true;

    /// <summary>
    /// false = **不写渲染快照**。只用于把两笔预算分开测，见
    /// 「先分两层测，不要混在一起」：
    ///
    ///   · **模拟侧**（带推进 + 机器 + 电力 + 流体）：判据 **&lt; 3ms**
    ///   · **快照填充**（前缀和 + 写 SoA，O(可见物品数)）：另一笔预算，与模拟能否做到
    ///     O(带数) **无关**
    ///
    /// 不把它们分开，百万规模的数字就是两笔混在一起的，无法判断到底哪一边超标。
    /// 与本项目既有的 <c>PowerIncremental</c> / <c>CursorEnabled</c> 同类：只影响测量，不影响语义。
    /// </summary>
    public bool SnapshotEnabled { get; set; } = true;

    /// <summary>
    /// true = **按视口剔除**：只把与可见范围相交的带/机器/管道/物品写进快照。
    ///
    /// **默认 false**，因为它是**渲染优化**而不是模拟语义：
    ///   · 关掉时行为与引入剔除之前**逐位一致**（所有既有探针都不受影响）；
    ///   · 它不进 <see cref="StateHash"/>、不影响任何模拟决策
    ///     ⇒ 同一份布局在不同视口下的**模拟结果完全相同**，只有画出来的东西不同。
    ///
    /// 为什么剔除必须做在**模拟线程的快照填充里**（而不是 Godot 提交前）：
    ///   百万实例的填充成本实测 25~36ms，而绝大部分实例根本不在屏幕上。
    ///   等到提交前才剔除，那笔前缀和 + 写 SoA 的钱已经花掉了。
    ///
    /// ⚠ 前提是渲染线程要持续推 <see cref="CullViewport"/>；没推过时本开关**不生效**
    ///   （宁可多画，也不能因为「还不知道视口」就画空白）。
    /// </summary>
    public bool CullEnabled { get; set; }

    /// <summary>可见范围（渲染线程推、Sim 线程读）。见 <see cref="ViewportCull"/>。</summary>
    public ViewportCull CullViewport { get; } = new();

    /// <summary>
    /// **机器名表**（Sim 线程发布、渲染线程读）。见 <see cref="MachineLabelBoard"/>。
    /// 每次结构变更时重建一次 —— 名字来自内容表，位置来自布局，都在模拟侧手上。
    /// </summary>
    public MachineLabelBoard MachineLabels { get; } = new();

    /// <summary>
    /// 一个实例在屏幕上小于这么多像素就**开始 LOD 抽样**（而不是全剔掉）。
    ///
    /// 亚像素的实例引擎确实栅格化不出来，但**不能因此什么都不画**：
    /// 正确做法是让**一格代表一片**（见 <see cref="LodStrideFor"/>），
    /// 屏幕上保留工厂的轮廓与密度，同时实例数被压下来。
    /// </summary>
    private const float MinOnScreenPx = 1.0f;

    /// <summary>
    /// 当前布局里的**带格总数**（结构变更时在 <c>SyncTopology</c> 里更新一次，O(带数)）。
    /// LOD 的预算估算用它 —— 见 <see cref="LodStrideFor"/>。
    /// </summary>
    public int TopologyBeltCells { get; private set; }

    /// <summary>
    /// 当前缩放下的 LOD 步长 <c>s</c>：**每 s×s 格画一格**，而且**照真实瓦片画**（不换素色块、
    /// 不拉长成线、不放大）—— 拉远时看到的仍然是「厂区本身，只是稀疏一些」。
    ///
    /// ⚠ **不要**给 LOD 加「缩略图」式画法（放大成一片 / 素色块马赛克 / 一段带一条线）：
    ///   LOD 只做**抽样**这一件事。
    ///
    /// 两个条件取更粗的那个：
    ///   ① **像素条件**：一格小于一个像素时才抽样（<c>1/px</c>）；
    ///   ② **预算条件**：抽样后画出的实例数不能超过 <see cref="LodBudget"/>
    ///      （抽样密度是 1/s²，所以按 <c>总数 / s²</c> 估）。
    ///
    /// ⚠ 为什么必须有 ②：**真实瓦片在亚像素缩放下本来就只是一个暗点**，
    ///   所以「画得密」并不等于「看得清」，只会白付填充钱。
    ///   实测整图取景（zoom 0.024、可见 2452×1379 格）：
    ///   · s=4（预算 25 万）⇒ 画出 9.7 万实例，快照填充 **29.5ms**、逻辑掉到 **29 tick/s**；
    ///   · s=7（预算 6 万）⇒ 画出约 3 万实例，填充回到 **≈9ms**、逻辑满 60。
    ///   ⇒ 这个取景下的取舍是**帧率优先**：厂区的走向在 s=7 时仍然看得出来，
    ///     而 29 tick/s 会把模拟本身也拖慢（那才是真的"坏掉"）。
    /// </summary>
    private int LodStrideFor(float viewScale)
    {
        // 诊断：强制步长（0 = 自动）。**只影响"画多密"，不影响任何模拟状态**。
        // 用途是把两件长得一样的事分开：「亚像素画不出来」与「LOD 抽样画太稀」——
        // 两者症状都是"画面空"，但治法完全不同。
        if (LodStrideOverride > 0) return LodStrideOverride;

        float px = viewScale * CellSizePx;          // 一格在屏幕上有多少像素
        if (px >= MinOnScreenPx) return 1;

        int lod = Math.Clamp((int)MathF.Ceiling(MinOnScreenPx / MathF.Max(px, 1e-4f)), 2, 512);

        // 预算条件：画出的带面实例 ≈ 带格总数 / lod²（每 lod×lod 格画一格）。
        //
        // ⚠ **必须按真实带格数估，不能按快照容量估**。容量 = 带格×2 + 物品位（物品在
        //   亚像素时整层不画），所以按容量估会大将近一倍，把步长**推大一档**：
        //   百万场景整图取景（zoom 0.0245、一格 0.78 像素）实测——
        //     · 按容量估 ⇒ stride **7** ⇒ 画出 33,935 个实例 ⇒ **画面几乎是空的**；
        //     · 按真实带格估 ⇒ stride **2** ⇒ 画出 366,727 个 ⇒ **厂区结构一眼可见**，
        //       而且逻辑仍是 **60.2 tick/s**、渲染 70 FPS（快照填充 8.37ms / SetBuffer 6.96ms）。
        //   也就是说：旧预算把"看不见"当成省钱的代价付掉了，而实测根本不需要付。
        long total = Math.Max(TopologyBeltCells, 1);
        while (lod < 512 && total / ((long)lod * lod) > LodBudget) lod++;
        return lod;
    }

    /// <summary>
    /// LOD 抽样后**允许画出**的实例数上限（见 <see cref="LodStrideFor"/>）。
    ///
    /// 40 万是**量出来的**，不是拍的：百万场景整图取景下 stride=2 ⇒ 366,727 个实例
    /// ⇒ 快照填充 8.37ms（Sim 线程预算 16.67ms）、每帧上传 17.6MB、SetBuffer 6.96ms，
    /// 逻辑仍满 60 tick/s、渲染 70 FPS，而画面从"什么都看不见"变成"结构清楚"。
    /// 再往上（stride=1 ⇒ 246 万实例）就是 27.8 tick/s / 11 FPS / 134MB 每帧，不值。
    /// </summary>
    private const int LodBudget = 400_000;

    /// <summary>本帧生效的 LOD 步长（1 = 未抽样）。诊断/报告用。</summary>
    public int LodStride { get; private set; } = 1;

    /// <summary>
    /// **调试用**：强制 LOD 步长（0 = 自动，见 <see cref="LodStrideFor"/>）。
    /// 它与 <see cref="CullEnabled"/> 同类：只改"画多密"，不进状态哈希、不影响模拟决策。
    /// </summary>
    public int LodStrideOverride { get; set; }

    /// <summary>
    /// 上一次 <see cref="WriteSnapshot"/> 自身的耗时（ms）——**这才是「快照填充」那笔预算**。
    ///
    /// ⚠ 别把它和渲染侧的「桥接 staging」搞混：那是 `RenderBridge.Submit` 里
    ///   把快照展开成 MultiMesh 实例缓冲的托管循环，是**另一笔**成本。
    ///   两笔都在 60Hz 路径上，但发生在不同线程、由不同代码付。
    ///   实测（百万场景、关剔除）：这里是约 26ms，而 staging 只有约 0.2ms。
    /// </summary>
    public double LastSnapshotMs { get; private set; }

    /// <summary>
    /// 上一次快照填充**实际写出**的实例数，以及**被剔除掉**的实例数。
    /// 剔除率 = Culled / (Live + Culled)。
    /// </summary>
    public int LastCulledInstances { get; private set; }

    /// <summary>
    /// 整个布局的世界包围盒（像素）。**渲染侧必须用它来算相机的可见范围与 MultiMesh 的
    /// <c>CustomAabb</c>** —— 固定成 1920×1080 会在百万规模的场景里直接失效：
    /// 那个 AABB 只盖住世界左上角一小块，Godot 会把整个 MultiMesh 一次裁掉，
    /// 画面上什么都没有（而且**不报任何错**，与 §5.3 那次「全黑」同一类症状）。
    ///
    /// 它只随**结构变更**变化，所以按 <see cref="TopologyRevision"/> 缓存 ——
    /// 每帧遍历全部机器与带是不必要的（4,000 带 + 8,000 机器就是每帧 12,000 次迭代）。
    ///
    /// 空布局返回一个 1×1 的退化矩形（不是空），免得调用方拿到 (0,0,0,0) 再除出 NaN。
    /// </summary>
    public WorldRect WorldBounds
    {
        get
        {
            if (_worldBoundsRevision == TopologyRevision) return _worldBounds;

            float loX = float.MaxValue, loY = float.MaxValue;
            float hiX = float.MinValue, hiY = float.MinValue;

            void Hit(float x, float y)
            {
                if (x < loX) loX = x;
                if (x > hiX) hiX = x;
                if (y < loY) loY = y;
                if (y > hiY) hiY = y;
            }

            const float half = FactoryLayout.CellPx * 0.5f;
            for (int m = 0; m < _machineSlots; m++)
            {
                if (!_machineAlive[m]) continue;
                (float wx, float wy) = _layout.MachineAt(m)!.WorldCenter;
                Hit(wx - half, wy - half);
                Hit(wx + half, wy + half);
            }
            for (int b = 0; b < _beltSlots; b++)
            {
                if (!_beltAlive[b]) continue;
                WorldRect r = _beltBounds[b];
                Hit(r.LoX, r.LoY);
                Hit(r.HiX, r.HiY);
            }
            for (int i = 0; i < _layout.PipeSlotCount; i++)
            {
                FluidPipeDef? p = _layout.PipeAt(i);
                if (p == null) continue;
                foreach (GridPos c in p.Cells)
                {
                    (float wx, float wy) = FactoryLayout.GridToWorld(c);
                    Hit(wx - half, wy - half);
                    Hit(wx + half, wy + half);
                }
            }

            _worldBounds = loX > hiX ? new WorldRect(0f, 0f, 1f, 1f) : new WorldRect(loX, loY, hiX, hiY);
            _worldBoundsRevision = TopologyRevision;
            return _worldBounds;
        }
    }

    private WorldRect _worldBounds;
    private int _worldBoundsRevision = -1;

    /// <summary>
    /// **把每条带灌满**（压力测试 / 演示的起点用）。返回装入的件数。
    ///
    /// 为什么需要它：一个百万件的工厂要是靠上游慢慢喂，得跑好几分钟才到量
    /// —— 那没法当压力测试用（要测的是「到了百万件之后每帧多少钱」，
    /// 不是「多久能到百万件」）。这条让场景在**第 0 tick 就处于满负荷状态**。
    ///
    /// ⚠ 它是**纯状态构造**，不是模拟逻辑：
    ///   · 只在**模拟线程启动之前**调用（它直接改带的内容，跑起来再改就是数据竞争）；
    ///   · 完全确定性（同一布局 + 同一批物品编号 ⇒ 同一状态，无随机）；
    ///   · 不改变任何后续规则，只是把带的初始状态设成「压满」。
    ///
    /// 装的形态就是**压满**：item 0 顶在出口（gap[0] = 0），其后每件间隔 <see cref="MinGapSub"/>，
    /// 于是不变式 <c>sum(gap) + TailGap == LengthSub</c> 仍然成立（探针 16 会验），
    /// 而且这条带在直到第一件被取走之前都是「压死」状态 —— 正是真实满负荷工厂的样子。
    /// </summary>
    /// <param name="beltTypes">
    /// 逐带指定要装的物品 id —— **下标 = 带 id − <paramref name="onlyFromBeltIndex"/>**，
    /// 也就是「本次要灌的这一段里的第几条」（<paramref name="onlyFromBeltIndex"/> 为默认 0 时
    /// 就是带 id 本身）。传 null 或下标越界时用 <paramref name="fallbackType"/>。
    /// 真实产线里每段带装的东西不一样（矿 → 带上是矿石、熔炉 → 带上是铁板），
    /// 所以这里必须能逐带指定；一律装同一种会让下游机器「拒吃杂料」而停摆。
    ///
    /// ⚠ 下标是**相对** <paramref name="onlyFromBeltIndex"/> 的，不是绝对带 id：
    ///   数组表达的是「这段带各自装什么」，而不是「整个工厂每条带装什么」。
    ///   按绝对 id 取的话，「只灌后面一段」的调用方（数组只有 7 项）会**每一项都越界**，
    ///   那几条带于是全拿到 fallbackType ⇒ 陈列区所有物品长得**一模一样**。
    /// </param>
    /// <param name="fillPercent">
    /// 灌到容量的百分之几。
    ///
    /// ⚠ **不要默认灌满（100）**：全厂灌满 = 处处堵死 = **一件都不动**。
    ///   灌到七八成时，只要各级产能相等（流量守恒），带就一直**在流**、
    ///   总量也不再变 —— 「有动态感」和「稳在百万件」这两件事是靠它同时成立的。
    ///   灌满只适合测「极限堵塞」那一种形态。
    /// </param>
    public long PrimeBelts(ushort[]? beltTypes = null, ushort fallbackType = 1,
                           int fillPercent = 100, int onlyFromBeltIndex = 0)
    {
        long inserted = 0;
        int pct = Math.Clamp(fillPercent, 0, 100);
        for (int b = onlyFromBeltIndex; b < _beltSlots; b++)
        {
            if (!_beltAlive[b]) continue;
            GapLine belt = _belts[b]!;
            ushort type = beltTypes != null && (uint)(b - onlyFromBeltIndex) < (uint)beltTypes.Length
                ? beltTypes[b - onlyFromBeltIndex]
                : fallbackType;
            if (type == 0) continue;                       // 0 保留为「空」，跳过

            int cap = belt.Capacity * pct / 100;
            for (int i = 0; i < cap; i++)
            {
                // 顺序：插入 → 前进一个 MinGapSub，为下一次插入腾出入口端的空隙。
                // 前进量正好 = 物品占位，所以装完就是「一件挨一件」，长度也刚好用完。
                if (!belt.TryInsert(type)) break;
                for (int k = 0; k < MinGapSub; k++) belt.AdvanceOneSub();
                inserted++;
            }
        }
        return inserted;
    }

    /// <summary>true = 电力生效（默认）；false = 视为满供电（对照用）。</summary>
    public bool PowerEnabled { get; set; } = true;

    /// <summary>true = 增量维护电网需求（O(电网数)/tick）；false = 每 tick 全量聚合（O(机器数)/tick），对照用。</summary>
    public bool PowerIncremental { get; set; } = true;

    public SnapshotTripleBuffer Buffers { get; }
    public FactoryLayout Layout => _layout;
    public int MachineCount => _layout.MachineCount;
    public int BeltCount => _layout.BeltCount;
    public int PipeCount => _layout.PipeCount;
    public int LiveCount { get; private set; }
    public int ActiveCount { get; private set; }
    public long ShippedTotal { get; private set; }
    public int StepCount { get; private set; }

    /// <summary>结构性变更的次数（HUD/诊断用）。</summary>
    public int TopologyRevision { get; private set; }

    // ── 流体 ────────────────────────────────────────────────────────────────
    //
    // 模型：**管网 = 相邻管道格子的连通分量**，是一个共享汇量（有体积、有容量）。
    // 这与「带」是完全不同的模型 —— 带是独立的一进一出路径，管网是所有连着管子的机器共用的一池。
    //
    // 每 tick 的顺序固定为：**先所有源按 id 升序注入，再唤醒等流体的机器**。
    // 顺序固定是确定性的前提。
    /// <summary>每格管道能装多少单位流体。</summary>
    public const int FluidPerCell = 100;

    private int[] _fluidVolume = Array.Empty<int>();
    private int[] _fluidCapacity = Array.Empty<int>();
    private int[] _fluidNetworkOf = Array.Empty<int>();
    private int[] _fluidNetworkB = Array.Empty<int>();

    /// <summary>上一次重建时「每个管网有哪些格子」——结构变更时按它把水按格搬过去（见 RebuildFluid）。</summary>
    private List<GridPos>[] _fluidCellsOfNet = Array.Empty<List<GridPos>>();
    private int[] _fluidRate = Array.Empty<int>();
    private int[] _fluidPerItem = Array.Empty<int>();

    /// <summary>每个管网上等着流体的机器（按 id 升序，用于 W3 唤醒）。</summary>
    private int[][] _fluidWaitersByNet = Array.Empty<int[]>();

    /// <summary>某个管网的当前流体量与容量（HUD / 探针用）。</summary>
    public int FluidVolume(int net) => (uint)net < (uint)_fluidVolume.Length ? _fluidVolume[net] : 0;
    public int FluidCapacity(int net) => (uint)net < (uint)_fluidCapacity.Length ? _fluidCapacity[net] : 0;
    public int FluidNetworkCount => _layout.FluidNetworkCount;

    // ── 建造命令队列（UI 线程 → 模拟线程） ──────────────────────────────────
    private readonly ConcurrentQueue<BuildCommand> _buildQueue = new();
    private int _buildPowerPercent = 100;

    /// <summary>
    /// 占用位图。**整份替换**（不是就地改），所以 UI 线程可以安全地读一份快照做预检：
    /// 引用赋值是原子的，读到的一定是某个完整重建过的版本。
    /// </summary>
    private volatile OccupancyGrid _occupancy;

    public OccupancyGrid Occupancy => _occupancy;

    /// <summary>最近一次建造尝试的结果（UI 展示用）。模拟线程写，UI 线程读。</summary>
    public BuildResult LastBuildResult { get; private set; } = new(BuildOutcome.None, default, "尚未施工");

    /// <summary>被接受并生效的建造命令数。</summary>
    public int AppliedBuildCount { get; private set; }

    /// <summary>被拒绝的建造命令数（占用 / 越界 / 非法形状…）。</summary>
    public int RejectedBuildCount { get; private set; }

    /// <summary>
    /// 提交一条建造命令。**线程安全**，可以从 UI/渲染线程直接调。
    /// 真正的校验与生效发生在下一个 tick 的开头（见 <see cref="Tick"/>）。
    /// </summary>
    public void EnqueueBuild(BuildCommand command) => _buildQueue.Enqueue(command);

    /// <summary>每 tick 推进的子格数（HUD 与调参用）。</summary>
    public int SpeedSub { get; private set; }

    /// <summary>加工一件需要多少 tick（HUD 用；取第一台加工机的值）。</summary>
    public int WorkTicks { get; private set; }

    /// <summary>第一段存活带的格数（HUD 用）。没有带就是 0。</summary>
    public int CellsPerBelt { get; private set; }

    /// <summary>
    /// 带面滚动相位（0..1）。带子每 tick 走 speedSub 个子格 = speedSub*PxPerSub 像素，
    /// 折算成「多少个瓦片」再取小数。
    ///
    /// 这套美术的带面是**静态瓦片**（作者没画动画帧），所以流动感只能靠滚动 UV 表现；
    /// 相位由模拟按「带子实际走了多远」算出 ⇒ **画面速度与逻辑速度天然一致**，
    /// 不需要手工对齐两套速度。
    /// </summary>
    public float BeltScrollPhase
    {
        get
        {
            if (ForcedScrollPhase is { } forced) return forced;
            float v = StepCount * (SpeedSub * PxPerSub) / CellSizePx;
            return v - MathF.Floor(v);
        }
    }

    /// <summary>
    /// 诊断用：把滚动相位**钉死**成固定值（null = 照常按 tick 算）。
    ///
    /// 为什么需要它：相位是时间的函数，截图那一刻的相位不可控 ——
    /// 而「带面到底滚的是哪个轴」这类判据在**相位为 0 时两种实现画得一模一样**，
    /// 那时测了等于没测（实测撞上过：截图恰好落在 tick 是 8 的倍数那一帧）。
    /// 钉成 0.5 就永远停在最能分辨的位置上（瓦片正好错开半格），判据可复现。
    /// </summary>
    public float? ForcedScrollPhase { get; set; }

    /// <summary>
    /// 演示布局的便捷构造 —— 等价于「用 <see cref="FactoryLayout.BuildDemo"/> 造一份布局再交给主构造」。
    /// 探针与场景都走这里，所以演示拓扑的变化不会影响它们。
    /// </summary>
    public FactorySim(
        int chains,
        int stages,
        int cellsPerBelt = 24,
        int speedSub = 2,
        int workTicks = 60,
        int producerWorkTicks = -1,
        int typeCount = 8,
        float unitSizePx = 32f,
        float worldHeight = 1080f,
        int demandPerMachine = 10,
        int powerPercent = 100)
        : this(
            FactoryLayout.BuildDemo(chains, stages, cellsPerBelt, workTicks,
                                    producerWorkTicks > 0 ? producerWorkTicks : workTicks, typeCount),
            speedSub, typeCount, unitSizePx, demandPerMachine, powerPercent)
    {
    }

    /// <summary>
    /// 主构造：从一份布局数据建模拟。
    ///
    /// <paramref name="unitSizePx"/> **必须与 Godot 侧 ItemSizePx 一致**：
    /// 快照里的 Scale 是「相对基础边长」的倍率，两边不一致会把机器与带面一起缩放错。
    /// </summary>
    public FactorySim(
        FactoryLayout layout,
        int speedSub = 2,
        int typeCount = 8,
        float unitSizePx = 32f,
        int demandPerMachine = 10,
        int powerPercent = 100,
        int maxInstances = DefaultMaxInstances,
        ContentDb? content = null)
    {
        if (speedSub < 1) throw new ArgumentOutOfRangeException(nameof(speedSub));
        if (typeCount < 1) throw new ArgumentOutOfRangeException(nameof(typeCount));

        _layout = layout;
        _content = content ?? ContentDb.Default;
        SpeedSub = speedSub;
        _typeCount = typeCount;
        _demandPerMachine = demandPerMachine;
        // ⚠ `unitSizePx` 是**故意不存**的：世界单位现在固定 = CellSizePx（一格 = 32px），
        //   快照里的尺寸一律**以格为单位**，所以给什么都不影响渲染。
        //   参数保留只为不改 20 多个探针的调用点。

        // 布局侧先算好一次电网，之后每次结构变更再 Resolve 一次
        _layout.Resolve();

        AllocArrays(_layout.MachineSlotCount, _layout.BeltSlotCount);

        // ⚠ 快照容量**不能只按当前布局算**。
        //   玩家是从空地图开始建的，如果按「初始布局 + 一点余量」给容量，
        //   建厂第一步就会因为「带太长」被拒 —— 探针 06 就是这么抓到的：
        //   空布局下容量只有 16，于是超过 8 格的带一律失败。
        //   所以容量取 max(按布局算出来的, maxInstances)，maxInstances 是「允许建多大」的上限。
        int maxCells = 0;
        for (int b = 0; b < _beltSlots; b++)
        {
            BeltDef? d = _layout.BeltAt(b);
            if (d != null && d.Cells.Length > maxCells) maxCells = d.Cells.Length;
        }
        CellsPerBelt = maxCells;

        // 快照容量 = **每帧最多写出多少个实例**（口径见 SnapshotInstanceNeed）。
        //
        // ⚠⚠ **不要**退回「按最长那条带 × 带条数」估的悲观上界：百万场景实测那样会算出
        //   11,449,647 实例（三缓冲约 1.6 GB），而实际只需要 3,128,487 —— 差 3.66 倍。
        //   「将来还要建」的余量由 maxInstances 与下面的 Max 负责：
        //   交互建造场景靠 DefaultMaxInstances(65,536) 兜底，空地图也不会被卡住。
        int capacity = Math.Max(SnapshotInstanceNeed(), maxInstances);
        Buffers = new SnapshotTripleBuffer(capacity);

        _buildPowerPercent = powerPercent;
        _maxInstances = capacity;
        SyncTopology(powerPercent);

        // 占用位图按世界给一个够大的范围（玩家能建的地方）；超出即「越界不可放」
        _occupancy = new OccupancyGrid(OccupancyWidth, OccupancyHeight);
        _occupancy.Rebuild(_layout);

        RecomputeActiveCount();
    }

    /// <summary>
    /// 快照实例上限的默认值。太小 ⇒ 建不了大厂（会以明确的错误信息告诉你）；
    /// 太大 ⇒ 白占内存（三缓冲 ⇒ 每实例约 12 float × 4B × 3）。
    /// 65536 实例 ≈ 9.4 MB，够建一个中等规模的厂。
    /// </summary>
    public const int DefaultMaxInstances = 65536;

    private readonly int _maxInstances;

    /// <summary>占用位图的默认范围（格）。世界 1920×1080 / 32px = 60×34，留足余量。</summary>
    public const int OccupancyWidth = 256;
    public const int OccupancyHeight = 256;

    /// <summary>按槽位数分配（或增长）所有下标平行数组。</summary>
    private void AllocArrays(int machineSlots, int beltSlots)
    {
        if (machineSlots > _machineSlots)
        {
            _machineSlots = machineSlots;
            Array.Resize(ref _machineAlive, machineSlots);
            Array.Resize(ref _defOfMachine, machineSlots);
            Array.Resize(ref _inBelts, machineSlots);
            Array.Resize(ref _outBelts, machineSlots);
            Array.Resize(ref _splitCursor, machineSlots);
            Array.Resize(ref _state, machineSlots);
            Array.Resize(ref _progress, machineSlots);
            Array.Resize(ref _work, machineSlots);
            Array.Resize(ref _carried, machineSlots);
            Array.Resize(ref _active, machineSlots);
            Array.Resize(ref _wired, machineSlots);
            Array.Resize(ref _networkOf, machineSlots);
            Array.Resize(ref _recipeOf, machineSlots);
            Array.Resize(ref _storageSlots, machineSlots);
            Array.Resize(ref _facing, machineSlots);
            Array.Resize(ref _outPushed, machineSlots);
            Array.Resize(ref _craftsStarted, machineSlots);
            Array.Resize(ref _craftsFinished, machineSlots);
            // 缓冲是**平铺定长**的（基址 = m * InvSlots）⇒ 增长时旧内容原地保留，
            // 不需要任何搬迁代码。这正是选定长布局的原因。
            Array.Resize(ref _inv, machineSlots * InvSlots);
            for (int i = 0; i < machineSlots; i++)
            {
                _inBelts[i] ??= Array.Empty<int>();
                _outBelts[i] ??= Array.Empty<int>();
            }
        }
        if (beltSlots > _beltSlots)
        {
            _beltSlots = beltSlots;
            Array.Resize(ref _beltAlive, beltSlots);
            Array.Resize(ref _defOfBelt, beltSlots);
            Array.Resize(ref _belts, beltSlots);
            Array.Resize(ref _beltCellSprites, beltSlots);
            Array.Resize(ref _beltCellRot, beltSlots);
            Array.Resize(ref _beltDownstream, beltSlots);
            Array.Resize(ref _beltBounds, beltSlots);
            Array.Resize(ref _transferFrom, beltSlots);
        }
    }

    /// <summary>布局里所有带的格子总数（LOD 预算要用它，而不是快照容量）。</summary>
    private int TotalBeltCells()
    {
        int n = 0;
        for (int b = 0; b < _beltSlots; b++)
            if (_layout.BeltAt(b) is { } bd) n += bd.Cells.Length;
        return n;
    }

    /// <summary>
    /// 布局里所有管道的**格子**总数。
    ///
    /// ⚠ 不能用 <see cref="FactoryLayout.PipeSlotCount"/>（那是**段数**）——
    ///   快照对管道是**逐格**写实例的（见 FactorySim.Snapshot.cs 的 <c>Pipes()</c>），
    ///   而一段管道往往有好几格。按段算会少算 Σ(格数−1)，缓冲填满之后
    ///   机器与物品就**静默画不出来**（各层循环以 <c>Live &lt; _cap</c> 为条件），而且不报任何错。
    /// </summary>
    private int TotalPipeCells()
    {
        int n = 0;
        for (int i = 0; i < _layout.PipeSlotCount; i++)
            if (_layout.PipeAt(i) is { } pd) n += pd.Cells.Length;
        return n;
    }

    /// <summary>
    /// 一帧最多要写多少个实例 = 机器 + 带面与物品（每条带每格各一个）+ 管道格。
    /// **构造函数算容量**与 <see cref="SyncTopology"/> 的**上限断言必须共用这一个函数**。
    /// </summary>
    private int SnapshotInstanceNeed() => _machineSlots + TotalBeltCells() * 2 + TotalPipeCells();

    /// <summary>
    /// **结构性变更之后必须调用**：把布局的变化同步进模拟，并且**保留存活机器的全部状态**
    /// （加工进度、携带物、带上物品都原样不动）。
    ///
    /// 新增的机器按种类初始化（生产者直接进 Working）；已被删除的机器作废并清空其状态。
    /// 电网表重建一次（O(机器数)）—— 结构变更是玩家操作级别的事件，不是每帧事件，
    /// 所以这里不做增量优化；**这个取舍由探针 05 的实测支撑**。
    /// </summary>
    public void SyncTopology(int powerPercent = 100)
    {
        _layout.Resolve();
        AllocArrays(_layout.MachineSlotCount, _layout.BeltSlotCount);

        for (int m = 0; m < _machineSlots; m++)
        {
            MachineDef? def = _layout.MachineAt(m);
            if (def == null)
            {
                if (_machineAlive[m])
                {
                    _machineAlive[m] = false;
                    _active[m] = false;
                    _state[m] = (byte)MachineState.WaitingInput;
                    _progress[m] = 0;
                    _carried[m] = 0;
                    _work[m] = 0;
                    _networkOf[m] = 0;
                    _recipeOf[m] = -1;
                    _storageSlots[m] = 0;
                    _outPushed[m] = 0;
                    _craftsStarted[m] = 0;
                    _craftsFinished[m] = 0;
                    Array.Clear(_inv, m * InvSlots, InvSlots);
                }
                _defOfMachine[m] = null;      // 槽位空了 ⇒ 下次放上来的算新机器
                continue;
            }

            _networkOf[m] = def.NetworkId;
            _work[m] = def.WorkTicks;
            _inBelts[m] = def.InBelts;
            _outBelts[m] = def.OutBelts;
            _recipeOf[m] = def.RecipeIndex;
            _storageSlots[m] = Math.Clamp(def.StorageCapacity, 0, InvSlots);
            _facing[m] = def.Facing;

            // 「这格是不是换了个东西」—— 比 **def 实例身份**，不看 _machineAlive。
            // 理由见 _defOfMachine 字段说明：同一批里先拆后放时 _machineAlive 从头到尾都是 true。
            bool isNew = !ReferenceEquals(_defOfMachine[m], def);

            if (isNew)
            {
                _machineAlive[m] = true;
                _state[m] = (byte)MachineState.WaitingInput;
                _progress[m] = 0;
                _carried[m] = def.InitialItemType;
                _active[m] = false;
                _splitCursor[m] = 0;
                _outPushed[m] = 0;
                _craftsStarted[m] = 0;
                _craftsFinished[m] = 0;
                Array.Clear(_inv, m * InvSlots, InvSlots);

                if (def.Kind == MachineKind.Producer && def.RecipeIndex < 0)
                {
                    // 透传生产机：没有输入带 ⇒ 不能停在 WaitingInput（那条路径会因为没输入带
                    // 直接 return，永不开始加工，症状就是「出货 0」）。直接进 Working。
                    _active[m] = true;
                    SetStateRaw(m, MachineState.Working);
                }
                else if (def.RecipeIndex >= 0)
                {
                    // 配方机：先让它跑一次 WaitingInput —— 无输入的配方（采矿）会当场开工，
                    // 有输入的配方会因为凑不齐而把自己睡下、等 W1。两条路都只需要这一句。
                    _active[m] = true;
                }
            }

            // ── **接线闸门**（任何机器都要绑定轨道才能用）────────────
            // 算一次、每 tick 只读一个布尔：没接够的机器**不工作** —— 不产出、不取料、不推货。
            //
            // ⚠ 必须放在"新机器初始化"**之后**、并且**区分新旧**：
            //   · 新机器：按种类初始化（上面那段）已经决定了要不要活跃，接线只负责"要不要工作"；
            //   · 老机器：接线状态**变了**才动 `_active`。
            //   ⚠ 不区分的话，**每台新机器**（连水塔/泵这类不需要轨道角色的也算）
            //   都会被"踢"成活跃 ⇒ 它们开始跑 TickMachine 的透传路径，
            //   把"没接线就不工作"变成"没接线照样动"。
            bool wired = def.IsWired;
            if (isNew)
            {
                _wired[m] = wired;
                if (!wired)
                {
                    _active[m] = false;
                    _state[m] = (byte)MachineState.WaitingInput;
                }
            }
            else if (wired != _wired[m])
            {
                _wired[m] = wired;
                if (!wired)
                {
                    _active[m] = false;          // 拔线就停：免得它留在活跃集里空转
                    _state[m] = (byte)MachineState.WaitingInput;
                }
                else
                {
                    // 接上线要**踢一下**：否则它可能永远醒不过来 —— 唤醒边是按"带上有货 /
                    // 出口腾空 / 管网来水"触发的，而**采矿机这类没有输入带的机器**根本没有唤醒边
                    // （否则接了线也可能 4000 tick 一件都不出）。
                    // ⚠ 状态也要一并复位成"新机器"那套：透传生产机（无配方）**不能**停在
                    //   WaitingInput（那条路径因为没有输入带会直接 return）—— 少了这一句，
                    //   探针 06 的"从零建产线"就是"接线正确但一件不出"。
                    _active[m] = true;
                    SetStateRaw(m, def.Kind == MachineKind.Producer && def.RecipeIndex < 0
                        ? MachineState.Working
                        : MachineState.WaitingInput);
                }
            }

            // 输入/输出带的条数变了（玩家接了新的带）⇒ 轮转游标要夹回范围
            if (_splitCursor[m] >= _outBelts[m].Length) _splitCursor[m] = 0;

            // ⚠ 仓储箱**常驻活跃**。
            //   它的两个流（进、出）是独立的，要让它正确休眠就得在两个方向上各维护一条
            //   唤醒条件 —— 那是四条新唤醒边，漏一条的症状又是「箱子静默卡住」。
            //   而箱子本身的计算量只是一次 24 槽扫描，**不值得为它引入四条易错的边**。
            //   这是一个显式取舍，不是遗漏。
            if (def.Kind == MachineKind.Storage) _active[m] = true;

            _defOfMachine[m] = def;      // 记住这格现在是哪个实例（下一轮靠它认「换了没有」）
        }

        for (int b = 0; b < _beltSlots; b++)
        {
            BeltDef? def = _layout.BeltAt(b);
            if (def == null)
            {
                if (_beltAlive[b])
                {
                    _beltAlive[b] = false;
                    _belts[b] = null;
                }
                _defOfBelt[b] = null;      // 槽位空了 ⇒ 下次放上来的算新带
                continue;
            }
            // 与机器同理：同一批里先拆后放时 _beltAlive 一直是 true，
            // 只有比 def 实例身份才认得出「这格换了另一条带」——
            // 否则新带会沿用旧的 GapLine（旧的长度/容量/存量，件数能超出新带的格数）。
            if (!_beltAlive[b] || _belts[b] == null || !ReferenceEquals(_defOfBelt[b], def))
            {
                int cells = def.Cells.Length;
                _belts[b] = new GapLine(cells, cells * SubdivPerCell, MinGapSub);
                _beltAlive[b] = true;
            }
            _defOfBelt[b] = def;
        }

        RebuildBeltTiles();
        RebuildBeltLinks();

        RebuildPower(powerPercent);
        RebuildFluid();

        WorkTicks = 0;
        CellsPerBelt = 0;
        for (int m = 0; m < _machineSlots; m++)
        {
            MachineDef? def = _layout.MachineAt(m);
            if (def != null && def.Kind == MachineKind.Processor) { WorkTicks = def.WorkTicks; break; }
        }
        for (int b = 0; b < _beltSlots; b++)
        {
            BeltDef? d = _layout.BeltAt(b);
            if (d != null && d.Cells.Length > CellsPerBelt) CellsPerBelt = d.Cells.Length;
        }

        // 上限检查：每帧写出的实例数不能超过快照容量。
        // **必须与构造函数共用同一个口径函数** —— 各写一份的话，口径一歪就会在构造完
        // 立刻抛「快照容量不足」，而那个数正是错口径算出来的。
        int cap = SnapshotInstanceNeed();
        if (cap > Buffers.Capacity) throw new InvalidOperationException(
            $"快照容量不足（需要 {cap}，当前 {Buffers.Capacity}）—— 建布局时就该按上限申请。");

        // LOD 的预算估算要用**这一帧真正可能画出的带格数**（见 LodStrideFor）：
        // 用「快照容量」估会大 2 倍（容量 = 带格 ×2 + 物品位），于是步长被推大一档 ——
        // 实测百万场景整图取景因此算成 stride=7（画面空），而按真实带格算是 stride=2（画面清楚）。
        TopologyBeltCells = TotalBeltCells();

        TopologyRevision++;
        PublishMachineLabels();
    }

    /// <summary>
    /// 重建并发布**机器名表**（结构变更时调一次，不是每帧）。
    ///
    /// ⚠ 必须在**模拟线程**上做：它读 <c>_layout</c>（模拟线程的数据）。
    ///   渲染线程只读发布出去的那份不可变数组，所以没有竞争。
    /// </summary>
    private void PublishMachineLabels()
    {
        var list = new List<MachineLabelBoard.Entry>(_machineSlots);
        for (int m = 0; m < _machineSlots; m++)
        {
            MachineDef? def = _layout.MachineAt(m);
            if (def == null) continue;
            (float wx, float wy) = FactoryLayout.GridToWorld(def.Pos);
            list.Add(new MachineLabelBoard.Entry(wx, wy, _content.NameOfSpriteCell(def.SpriteCell)));
        }
        MachineLabels.Publish(list.ToArray());
    }

    public MachineState StateOf(int m) => (MachineState)_state[m];

    /// <summary>加工进度（千分比·tick）。探针用它验证「结构变更不扰动存活机器」。</summary>
    public int ProgressOf(int m) => _progress[m];

    /// <summary>机器槽位数（含空洞）。探针用它确认槽位复用。</summary>
    public int MachineSlotCount => _machineSlots;
    public int BeltItems(int b) => _belts[b]?.Count ?? 0;

    /// <summary>这条带**累计被插入过多少件**（分流是否均匀就靠它衡量，比看瞬时件数干净）。</summary>
    public long BeltInsertedTotal(int b) => _belts[b]?.InsertedTotal ?? 0;

    /// <summary>这条带**累计被取出过多少件**。守恒校验用：插入 − 取出 必须等于现存。</summary>
    public long BeltRemovedTotal(int b) => _belts[b]?.RemovedTotal ?? 0;

    /// <summary>带上第 k 个物品（0 = 最靠近出口）的类型；越界/空格返回 0。</summary>
    public int BeltTypeAt(int b, int k)
    {
        GapLine? l = _belts[b];
        return l != null && (uint)k < (uint)l.Count ? l.TypeOf(k) : 0;
    }

    /// <summary>机器的输出带条数（分流器应 ≥2）。</summary>
    public int OutBeltCount(int m) => _outBelts[m].Length;

    /// <summary>机器的输入带条数（合流器应 ≥2）。</summary>
    public int InBeltCount(int m) => _inBelts[m].Length;
    public int BeltGapZero(int b) { GapLine? l = _belts[b]; return l != null && l.Count > 0 ? l.GapAt(0) : -1; }
    public int BeltTailGap(int b) => _belts[b]?.TailGapSub ?? 0;
    public int SatOfNetwork(int c) => _networkSat[c];

    /// <summary>
    /// 第一个电网的供电率（千分比）；**没有电网时返回满供电**。
    /// HUD 用它 —— 空地图开局时一个电网都没有，直接读 <c>SatOfNetwork(0)</c> 会越界。
    /// </summary>
    public int SatOfFirstNetwork => _networkSat.Length > 0 ? _networkSat[0] : SatFull;
    public int DemandOfNetwork(int c) => _networkDemand[c];
    public int GenerationPerNetwork => _netCount > 0 ? _netGen[0] : 0;

    // ── 配方 / 机器缓冲（探针与 HUD 用） ────────────────────────────────────
    public ContentDb Content => _content;

    /// <summary>机器用的配方；没有就是 null。</summary>
    public RecipeDef? RecipeOfMachine(int m) => _content.RecipeOf(_recipeOf[m]);

    /// <summary>这台配方机开工过几次（输入已扣、还在加工）。不进状态指纹，见字段说明。</summary>
    public int RecipeCraftsStarted(int m) => (uint)m < (uint)_machineSlots ? _craftsStarted[m] : 0;

    /// <summary>这台配方机完工过几次（产出已全部推出）。守恒校验要用它算产出，不能用开工数。</summary>
    public int RecipeCraftsFinished(int m) => (uint)m < (uint)_machineSlots ? _craftsFinished[m] : 0;

    /// <summary>
    /// 机器手里**还拿着几件**（0 或 1）。物料守恒用它把「已从带上取走、还没送到下游」的那件算进去。
    ///
    /// ⚠ 不要直接拿 <c>_carried</c> 当数量用 —— 它存的是**物品类型编号**，
    ///   把它当件数会得到「出货机手上拿着 6 件」这种荒谬结果（6 是电路板这个物品的 id）。
    ///   这个坑是探针 09 的守恒等式抓出来的。
    ///
    /// ⚠ 只在加工中 / 输出被堵时才为 1：透传机器加工完不会清 <c>_carried</c>，
    ///   空闲时它仍留着上一件的类型编号。配方机不靠 <c>_carried</c> ——
    ///   它的在制品是「已扣输入、未出产出」，由开工/完工两个计数体现。
    /// </summary>
    public int CarriedCount(int m)
    {
        if ((uint)m >= (uint)_machineSlots) return 0;
        if (_recipeOf[m] >= 0) return 0;
        return (MachineState)_state[m] == MachineState.WaitingInput ? 0 : 1;
    }

    /// <summary>机器手里那件的物品类型（0 = 没有）。只在加工中/被堵时有意义，见 <see cref="CarriedCount"/>。</summary>
    public int CarriedTypeOf(int m) => CarriedCount(m) > 0 ? _carried[m] : 0;

    /// <summary>机器在输入缓冲里攒了多少件（配方机 = 当前一件的部分输入，箱子 = 存货）。</summary>
    public int InvCount(int m)
    {
        int baseIdx = m * InvSlots, n = 0;
        for (int k = 0; k < InvSlots; k++) if (_inv[baseIdx + k] != 0) n++;
        return n;
    }

    /// <summary>机器缓冲第 k 槽的物品 id（0 = 空）。</summary>
    public int InvTypeAt(int m, int k)
        => (uint)m < (uint)_machineSlots && (uint)k < InvSlots ? _inv[m * InvSlots + k] : 0;

    public int FacingOf(int m) => (uint)m < (uint)_machineSlots ? _facing[m] : 0;

    /// <summary>这台机器接够线了吗（没接够 ⇒ 不工作）。HUD / 探针用。</summary>
    public bool IsMachineWired(int m) => (uint)m < (uint)_machineSlots && _wired[m];

    /// <summary>还没接够线的机器台数（HUD 一眼看出"还有几台没接"）。</summary>
    public int UnwiredMachineCount
    {
        get
        {
            int n = 0;
            for (int m = 0; m < _machineSlots; m++)
                if (_machineAlive[m] && !_wired[m]) n++;
            return n;
        }
    }

    /// <summary>第二个相邻管网（泵用）。</summary>
    public int FluidNetworkBOf(int m) => (uint)m < (uint)_machineSlots ? _fluidNetworkB[m] : -1;

    /// <summary>把「这台机器有没有在等流体」暴露出来 —— 探针用它区分「等料」与「等水」。</summary>
    public bool WaitsForFluid(int m) => (uint)m < (uint)_machineSlots && _fluidPerItem[m] > 0;

    /// <summary>带上物品总数（HUD 用）。由 <see cref="WriteSnapshot"/> 顺手统计，不额外遍历。</summary>
    public int ItemsOnBelts { get; private set; }

    /// <summary>按种类统计存活机器，返回 HUD 用的一行摘要（例如「加工 4 · 物流 2 · 流体 1」）。</summary>
    public string MachineKindCounts()
    {
        Span<int> n = stackalloc int[16];
        for (int m = 0; m < _machineSlots; m++)
        {
            if (!_machineAlive[m]) continue;
            MachineDef? d = _layout.MachineAt(m);
            if (d == null) continue;
            n[(int)d.Kind]++;
        }

        var parts = new List<string>();
        for (int k = 0; k < n.Length; k++)
        {
            if (n[k] == 0) continue;
            parts.Add($"{KindName((MachineKind)k)} {n[k]}");
        }
        return parts.Count == 0 ? "无" : string.Join(" · ", parts);
    }

    private static string KindName(MachineKind k) => k switch
    {
        MachineKind.Producer => "生产",
        MachineKind.Processor => "加工",
        MachineKind.Shipper => "出货",
        MachineKind.Splitter => "分流",
        MachineKind.Merger => "合流",
        MachineKind.FluidSource => "流体源",
        MachineKind.FluidUser => "耗水",
        MachineKind.Storage => "仓储",
        MachineKind.Inserter => "机械臂",
        MachineKind.Pump => "泵",
        _ => k.ToString(),
    };

    /// <summary>剪贴板里的机器（**相对坐标** + 定义），给蓝图预览用。</summary>
    public IEnumerable<(GridPos Rel, MachineDef Def)> ClipboardMachineDefs()
    {
        foreach (BlueprintMachine bm in _clipMachines) yield return (bm.Rel, bm.Def);
    }

    /// <summary>剪贴板里的带（相对坐标数组），给蓝图预览用。</summary>
    public IEnumerable<GridPos[]> ClipboardBeltCells()
    {
        foreach (GridPos[] cells in _clipBelts) yield return cells;
    }

    /// <summary>剪贴板里第 <paramref name="i"/> 条带（相对坐标数组）—— 预览要按层排序时按序号取。</summary>
    public GridPos[] ClipboardBeltCells(int i) => _clipBelts[i];

    public void RecomputeActiveCount()
    {
        int n = 0;
        for (int m = 0; m < _machineSlots; m++) if (_machineAlive[m] && _active[m]) n++;
        ActiveCount = n;
    }

    public void Step(float dt) => Tick();

    /// <summary>推进一个逻辑 tick。**只在 Sim 线程调用。**</summary>
    public void Tick()
    {
        // ⓪ 排空建造命令 —— **必须在 tick 边界、且在其它步骤之前**。
        //    这样一次建造（可能含多步：加机器 + 接带 + 重建电网）要么整个在上一 tick 之后
        //    生效、要么整个在下一 tick 之前生效，模拟永远看不到「半应用」的拓扑。
        DrainBuildQueue();

        // ① 电力求解（用上一 tick 结束时的需求快照 ⇒ 恒定 1 tick 延迟，确定性）
        SolvePower();

        // ①′ 流体：先源注入、再 W3 唤醒
        SolveFluid();

        // ② 带推进（不自动取出；取出由消费者机器负责）
        for (int b = 0; b < _beltSlots; b++)
        {
            if (!_beltAlive[b]) continue;
            GapLine belt = _belts[b]!;
            for (int k = 0; k < SpeedSub; k++) belt.AdvanceOneSub();
        }

        // ③ 唤醒边 —— 判据是「带推进之后」的状态，必须紧跟在 ② 后面
        if (WakeEdgesEnabled) EvaluateWakeEdges();

        // ③′ 带 → 带 的移交（**在唤醒边之后、机器之前**）。
        //     顺序理由：移交改变带的出口/入口状态，必须发生在「机器读带之前」，
        //     否则本 tick 到达的货要等下一 tick 才被机器看到，吞吐凭空少一档。
        TransferBetweenBelts();

        // ④ 机器，严格 id 序（休眠模式与全量模式等价的前提）
        if (UseActiveSet)
        {
            for (int m = 0; m < _machineSlots; m++)
                if (_machineAlive[m] && _active[m]) TickMachine(m);
        }
        else
        {
            for (int m = 0; m < _machineSlots; m++)
                if (_machineAlive[m]) TickMachine(m);
        }

        StepCount++;
        WriteSnapshot();
        RecomputeActiveCount();
    }

    /// <summary>
    /// 把一台正在睡的机器叫醒（W1 / W2 共用的那半步）。
    /// 不满足条件就什么也不做 —— 叫醒一个本就在跑的机器是**无害的重复**，不是错误。
    /// </summary>
    /// <param name="m">机器 id；<c>-1</c> 表示这条带的这一端没接机器。</param>
    /// <param name="waiting">它此刻必须处于哪个状态才叫得醒。</param>
    private void WakeIfSleeping(int m, MachineState waiting)
    {
        // 非法 id 由 (uint) 转换一并挡掉：-1 转 uint 会变成 4294967295，
        // 必然 ≥ 槽位数 —— 所以**不需要**再单独写一句 m >= 0。
        if ((uint)m >= (uint)_machineSlots) return;
        if (!_machineAlive[m] || _active[m]) return;
        if ((MachineState)_state[m] != waiting) return;

        _active[m] = true;
    }

    /// <summary>W1 / W2。这两条是**全部**的唤醒来源，漏一条就静默卡死。</summary>
    private void EvaluateWakeEdges()
    {
        for (int b = 0; b < _beltSlots; b++)
        {
            if (!_beltAlive[b]) continue;
            GapLine belt = _belts[b]!;
            BeltDef def = _layout.BeltAt(b)!;

            // W1：有物品到达出口 ⇒ 唤醒在等料的消费者
            if (belt.Count > 0 && belt.GapAt(0) == 0)
                WakeIfSleeping(def.ToMachine, MachineState.WaitingInput);

            // W2：入口有空间 ⇒ 唤醒被堵的生产者
            if (belt.TailGapSub >= MinGapSub)
                WakeIfSleeping(def.FromMachine, MachineState.BlockedOutput);
        }
    }

    /// <summary>
    /// **带 → 带** 的移交：某段带的出口格与另一段带的入口格边相邻时，把出口那件搬过去。
    ///
    /// ⚠ 必须是「**先收集意向、再统一应用**」，不能边走边搬：
    ///   若按 id 升序一边搬一边让下游接着搬，一件物品就能在同 tick 里穿过一整条链
    ///   （id 恰好递增时），物理上等于传送带瞬时无限快。收集后再应用就保证
    ///   **每段带每 tick 最多移交一件**，与机器之间的移交节奏一致。
    ///
    /// 确定性：意向按带 id 升序收集、按同序应用；下游满了就跳过（先到先得）。
    /// </summary>
    private void TransferBetweenBelts()
    {
        if (_beltDownstream.Length == 0) return;

        int intents = 0;
        for (int b = 0; b < _beltSlots; b++)
        {
            int next = _beltDownstream[b];
            if (next < 0) continue;
            GapLine a = _belts[b]!;
            // 只有「物品正好到出口」才谈得上移交 —— 与机器取货的判据完全一致
            if (a.Count == 0 || a.GapAt(0) != 0) continue;
            _transferFrom[intents++] = b;
        }

        for (int k = 0; k < intents; k++)
        {
            int b = _transferFrom[k];
            int next = _beltDownstream[b];
            GapLine a = _belts[b]!;
            GapLine c = _belts[next]!;
            if (!c.CanAccept) continue;                      // 下游满了 ⇒ 这一件留在原地
            if (a.Count == 0 || a.GapAt(0) != 0) continue;    // 前一次移交可能已经改变了它
            // CanAccept 与 TryInsert 的守卫**完全一致**（容量 + 入口端最小间距），
            // 且两次调用之间没有任何东西改动 c ⇒ 这里插入必然成功，不需要回滚分支。
            a.TryRemoveAtExit(out ItemSlot item);
            c.TryInsert(item.Type);
            BeltTransferCount++;
        }
    }

    private int SatOf(int m) => PowerEnabled ? _networkSat[_networkOf[m]] : SatFull;

    /// <summary>状态迁移的唯一入口 —— 电力的增量需求维护就挂在这里，避免漏掉某条路径。</summary>
    private void SetState(int m, MachineState next)
    {
        var prev = (MachineState)_state[m];
        if (prev == next) return;

        if (prev == MachineState.Working) _networkDemand[_networkOf[m]] -= _demandPerMachine;
        if (next == MachineState.Working) _networkDemand[_networkOf[m]] += _demandPerMachine;

        _state[m] = (byte)next;

        if (next == MachineState.Working) _progress[m] = _work[m] * SatFull;
    }

    /// <summary>状态指纹 —— 给「休眠模式 vs 全量模式」逐 tick 等价比对用，
    /// 也给「结构变更后是否与重新建一份等价」用。
    /// </summary>
    public ulong StateHash()
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }

        for (int m = 0; m < _machineSlots; m++)
        {
            if (!_machineAlive[m]) continue;
            Mix((ulong)m);
            Mix(_state[m]);
            Mix((ulong)(uint)_progress[m]);
            Mix(_carried[m]);
            Mix((ulong)(uint)_splitCursor[m]);   // 分流游标影响后续走向，必须进指纹
            Mix((ulong)(uint)_outPushed[m]);     // 多产出配方推到哪一件了，同样影响后续
            Mix((ulong)(uint)_facing[m]);
            // 输入缓冲也是状态：两台机器「进度一样但攒的料不同」不是同一个状态。
            // 仓储箱的全部状态就在这里，漏掉它的话箱子相关的等价比对会全部假通过。
            int baseIdx = m * InvSlots;
            for (int k = 0; k < InvSlots; k++) Mix(_inv[baseIdx + k]);
        }
        for (int b = 0; b < _beltSlots; b++)
        {
            if (!_beltAlive[b]) continue;
            GapLine belt = _belts[b]!;
            Mix((ulong)b);
            Mix((ulong)belt.Count);
            Mix((ulong)belt.TailGapSub);
            for (int i = 0; i < belt.Count; i++)
            {
                Mix((ulong)belt.GapAt(i));
                Mix(belt.TypeOf(i));
            }
        }
        // ⚠ **流体存量也必须进指纹**：它参与决策（TryDrawFluid 会因存量不足**拒绝让机器开工**），
        //   所以「结构变更后按格子把存量搬了过去」与「从零重建一份（存量 = 0）」是**两个行为
        //   不同的状态**。而本函数正是用来做那种等价比对的判据 —— 漏掉它，那种比对会假通过。
        //   （容量不进：它只由管网几何派生，与机器/带一样属于「布局」那一层，
        //     本函数只覆盖逐 tick 会变的模拟状态。）
        for (int n = 0; n < _fluidVolume.Length; n++) Mix((ulong)(uint)_fluidVolume[n]);
        Mix((ulong)ShippedTotal);
        return h;
    }
}
