using System;
using System.Diagnostics;
using Godot;
using ForgeFlow.Sim;
using ForgeFlow.Sim.Data;

using Environment = System.Environment;

// =============================================================================
// StressRunner —— **百万件物品压力测试场景**（Scenes/Stress.tscn）。
//
// 它存在的理由：`FortyFlow.Sim.Probes` 的探针 14/15 只能证明**模拟侧**与
// **快照填充**的数字，而百万规模要**两层分开测**：
// 渲染侧那一层必须有真实引擎参与（`MultiMesh.SetBuffer` 的 marshal + 上传、
// GPU 背压、`VisibleInstanceCount` 的行为）—— 探针里没有 Godot，测不到。
//
// 所以这个场景做三件事：
//   ① 真的建出一个**百万件物品**的工厂；
//   ② 每帧把「能看见哪一块世界」推给模拟，让它按视口剔除；
//   ③ 把「写出实例 / 剔除实例 / 剔除率 / 填充耗时 / SetBuffer 耗时 / 上传字节数」
//      持续打到控制台与 HUD —— 剔除方案与剔除后的数字都在这里。
//
// ⚠ 关键约束：节点与资源**全部在 Stress.tscn 里预建**，
//   本脚本只解析、连线、改属性。找不到节点就报错退出，不做动态兜底。
//
// 环境变量：
//   FORGEFLOW_STRESS_CHAINS=<n>   产线条数（默认 2850 ⇒ 带上物品约 101 万件）
//   FORGEFLOW_STRESS_LANES=<n>    每个蛇形盒子折几道（默认 8）
//   FORGEFLOW_STRESS_LANELEN=<n>  每道多少格（默认 70）
//   FORGEFLOW_STRESS_NO_FLUID=1   关掉洗矿线（不带水塔/管道）
//   FORGEFLOW_NO_LABELS=1         关掉机器名 label（由 MachineLabelDrawer 解析；
//                                 注意前缀是 FORGEFLOW_ 而不是 FORGEFLOW_STRESS_）
//   FORGEFLOW_STRESS_VERIFY=1     建完布局跑**重叠自检**（百万格要几十 MB 的哈希表）
//   FORGEFLOW_NO_CULL=1           **关掉剔除**（对照实验：数字必须显著变差）
//   FORGEFLOW_STRESS_PAN=1        让相机自动横扫（验证剔除随视口移动而变，且画面不闪空）
//   FORGEFLOW_PROBE=1             每秒打印一行统计
//   FORGEFLOW_SCREENSHOT / _AT / FORGEFLOW_AUTOEXIT   与主场景同语义
// =============================================================================

public partial class StressRunner : Node2D
{
    /// <summary>
    /// 默认 2850 条**蛇形**产线 × 每盒 8 道 × 每道 70 格。
    /// 每条产线的路径 ≈ 8×71−1 = 567 格 ⇒ 带格总数 ≈ 2850 × 563 ≈ **160 万格**。
    ///
    /// ⚠ 默认取 2850 而不是 2600：实测 2600 条时**带上物品只有 94.7 万**（占带格 69.9%），
    ///   达不到「百万压力测试」这个名字要的量；2850 条实测 **101.6 万件**。
    ///   调这个数要连 <c>packH</c> 一起看（折行高度上限是按 √条数 估的，条数涨得太快会排不下）。
    /// </summary>
    [Export] public int Chains = 2850;
    // ⚠ 这个字段**不影响布局**（蛇形布局的带长由 LaneLen × LaneCount 决定）。
    //   留着只是为了老跑测脚本传 `FORGEFLOW_STRESS_CELLS` 时不报错 —— 它不参与任何计算。
    //   想调带长请用 FORGEFLOW_STRESS_LANELEN（见文件头的环境变量说明）。
    [Export] public int CellsPerBelt = 60;

    private MultiMeshInstance2D? _items;
    private ColorRect? _grid;
    private MultiMesh? _multiMesh;
    private RenderBridge? _bridge;
    private FactorySim? _sim;
    private SimLoopRunner? _runner;
    private Camera2D? _camera;

    private Label? _hudStatus, _hudInstances, _hudTiming, _hudWorld, _hudHint;

    private readonly Stopwatch _submitClock = new();
    private double _elapsed;
    private double _lastReportSec;
    private int _framesSinceReport;
    private double _fillSumMs, _fillMaxMs;
    private double _setBufSumMs, _setBufMaxMs;
    private double _snapSumMs, _snapMaxMs;
    private double _submitSumMs, _submitMaxMs;
    private long _lastTicks;
    private double _lastTickSec;
    private long _repLastTicks;
    private long _repLastShipped;
    private double _repLastSec;

    /// <summary>
    /// 机器名 label 的绘制器（**与主场景共用同一份实现**，见 <see cref="MachineLabelDrawer"/>）。
    /// 数据来自 <c>FactorySim.MachineLabels</c> —— 模拟线程在结构变更时发布，
    /// 所以渲染线程不用（也不能）去读 <c>_layout</c>。
    /// </summary>
    private MachineLabelDrawer? _labels;

    /// <summary>相机可见的世界矩形（渲染线程自己算的，给 label 裁剪用）。</summary>
    private Rect2 _viewRect;

    /// <summary>本局的带格总数（算「占带格百分之几」用；由 BuildSim 填）。</summary>
    private int _totalBeltCells;

    private bool _probe;
    private bool _autoPan;

    /// <summary>本局世界的实际包围盒（相机取景、自动横扫、上传容量都用它，不写死）。</summary>
    private WorldRect _worldBounds;
    private bool _autoExitSet;
    private double _autoExitSec;
    private bool _shotTaken;
    private string? _screenshotPath;
    private double _screenshotAtSec = 25.0;

    public override void _Ready()
    {
        Chains = EnvInt("FORGEFLOW_STRESS_CHAINS", Chains);
        CellsPerBelt = EnvInt("FORGEFLOW_STRESS_CELLS", CellsPerBelt);
        _probe = Environment.GetEnvironmentVariable("FORGEFLOW_PROBE") == "1";
        _autoPan = Environment.GetEnvironmentVariable("FORGEFLOW_STRESS_PAN") == "1";

        if (EnvInt("FORGEFLOW_AUTOEXIT", 0) > 0)
        {
            _autoExitSet = true;
            _autoExitSec = EnvInt("FORGEFLOW_AUTOEXIT", 0);
        }
        _screenshotPath = Environment.GetEnvironmentVariable("FORGEFLOW_SCREENSHOT");
        if (double.TryParse(Environment.GetEnvironmentVariable("FORGEFLOW_SCREENSHOT_AT"), out double sa) && sa > 0)
            _screenshotAtSec = sa;

        // ── 解析场景里预建的节点（找不到就报错退出，不做动态兜底）────────────
        _items = GetNodeOrNull<MultiMeshInstance2D>("ItemMultiMesh");
        if (_items?.Multimesh == null)
        {
            GD.PushError("[stress] 找不到 ItemMultiMesh 或其 MultiMesh —— 场景配置漏了，退出。");
            GetTree().Quit(1);
            return;
        }
        _multiMesh = _items.Multimesh;
        _camera = GetNodeOrNull<Camera2D>("Camera");
        _grid = GetNodeOrNull<ColorRect>("GridBackground");

        _hudStatus = GetNodeOrNull<Label>("Hud/Panel/VBox/LineStatus");
        _hudInstances = GetNodeOrNull<Label>("Hud/Panel/VBox/LineInstances");
        _hudTiming = GetNodeOrNull<Label>("Hud/Panel/VBox/LineTiming");
        _hudWorld = GetNodeOrNull<Label>("Hud/Panel/VBox/LineWorld");
        _hudHint = GetNodeOrNull<Label>("Hud/Panel/VBox/LineHint");
        if (_hudStatus == null || _hudInstances == null || _hudTiming == null ||
            _hudWorld == null || _hudHint == null)
        {
            GD.PushError("[stress] HUD 节点不全 —— 场景配置漏了，退出。");
            GetTree().Quit(1);
            return;
        }

        BuildSim();

        _runner = new SimLoopRunner(_sim!);
        _runner.Start();

        // ⚠ 这里**不要**手动设相机位置。
        //   取景已经在 BuildSim() 里由 CameraController.FrameWorld 按**实际世界包围盒**算好了；
        //   写死 `Position = (640, 320)` 等于假设「世界左上角就是看点」——
        //   换一个规模的布局就停在空地上（而且会覆盖掉刚刚算好的取景）。

        _hudHint!.Text = "百万件压力测试场景 · WASD/滚轮移动相机看剔除率变化 · " +
                         "FORGEFLOW_NO_CULL=1 可关掉剔除做对照";

        // ⚠ 这里**不要**打 `Chains × CellsPerBelt`：CellsPerBelt **不影响布局**
        //   （见它自己的字段注释），那样报出来会比真实带格数少约 8.5 倍
        //   （实测真实值 1,452,504）。带格数的唯一真相在模拟侧：TopologyBeltCells。
        GD.Print($"[stress] 就绪：{Chains:N0} 链，带格共 {_sim!.TopologyBeltCells:N0}");
        GD.Print($"[stress] 视口剔除 = {(_sim!.CullEnabled ? "开" : "关")}（FORGEFLOW_NO_CULL=1 可关）");
        Report("startup");
    }

    /// <summary>
    /// 造百万规模的布局 —— **每行是一条真实产线**（各种机器 + 带 + 物品 + 管道）。
    ///
    /// 用**内容表里的可建造项**（<see cref="BuildableDef.ToMachineDef"/> ⇒ 带配方与真实图集格子）
    /// 组装产线 —— 只用「生产机 → 带 → 出货机」两台机器的话虽然也能压到百万件，
    /// 但长得不像工厂（只有两种精灵、物品也不流动）。
    ///
    ///   4 种产线轮换：
    ///     齿轮线：铁矿机 →带(矿石)→ 铁熔炉 →带(铁板)→ 齿轮机 →带(齿轮)→ 出货机
    ///     铜板线：铜矿机 →带(铜矿)→ 铜熔炉 →带(铜板)→ 出货机
    ///     洗矿线：铁矿机 →带(矿石)→ 洗矿机 →带(铁板)→ 出货机 + 水塔→管道→供水
    ///     铁板线：铁矿机 →带(矿石)→ 铁熔炉 →带(铁板)→ 出货机
    ///
    /// **交错感**（不是完全对齐的平行条带 —— 那样一眼就是程序生成的阵列）：
    ///   ① 方向交替：奇数行整条水平镜像（矿机在右、出货机在左）⇒ 人字纹；
    ///   ② 水平错位：每行起点错开 0/7/14/21 格 ⇒ 机器列不会竖着对齐成一条线；
    ///   ③ 长度抖动：每段带 ±4 格 ⇒ 各行右边缘参差；
    ///   ④ 街道：每 8 行多留一段空隙 ⇒ 街区节奏。
    /// 四个手段都**不改变**产线语义，只是摆放方式。
    ///
    /// 画面上于是同时有 8 种机器、5 种物品、带与管道 —— 就是「像别的工厂游戏」的样子；
    /// 而带的总格数仍然 ≈ 100 万，剔除/吞吐的压力测试意义不变。
    ///
    /// ⚠ **布局的世界尺寸远超占用位图（256×256）**：占用位图是「玩家能不能在这一格建造」
    ///   的校验层，只有交互建造才用；本场景是**直接构造布局**（不走命令队列），
    ///   所以位图越界不影响模拟与渲染（<c>Rebuild</c> 会静默跳过界外格）。
    /// </summary>
    private void BuildSim()
    {
        ContentDb content = MakeStressContent();
        var layout = new FactoryLayout();

        // 逐带记录「这条带上该装什么」——给 PrimeBelts 用。
        // ⚠ 顺序必须与 layout.AddBelt 的调用顺序**严格一致**（下标就是带 id）。
        var primeTypes = new List<ushort>();

        int beltCells = 0, pipeCells = 0, degenerate = 0;
        var typeCount = new int[LineTypes];
        var dirCount = new int[4];

        // ── 布局：**方块网格 × 蛇形盒子** ────────────────────────────────────
        //
        // 每个方块里叠 <see cref="BoxesPerBlock"/> 个**蛇形盒子**，相邻方块的方向差 90°
        // （棋盘格）⇒ 四向交错；而每条产线在自己的盒子里来回折
        // （<see cref="SerpentinePath"/>）⇒ **把盒子填满**，不再是细长的一条。
        //
        // 盒子的两个朝向共用同一份路径：横向方块直接用 (列,行)，
        // 纵向方块把它转置（<see cref="BoxMap"/> 的 transpose）⇒ 一份几何、四向通用。
        int boxW = LaneLen, boxH = 2 * LaneCount - 1;
        int boxPitch = boxH + 2;                       // 垂直/水平叠放时的间距
        // ⚠ **方块数要按「每块最少几个盒子」算**：凌乱化之后每块用 3~4 个盒子（哈希决定），
        //   按 4 算会**少放**产线 —— 实测只放下 2,307 条而不是 2,600 条
        //   （日志里四种数量各 577/577/577/576 就是这个症状）。
        //   这里按最少值 3 反推方块数，保证一定放得下。
        int minBoxes = BoxesPerBlock - 1;
        int blocks = (Chains + minBoxes - 1) / minBoxes;
        int cols = (int)Math.Ceiling(Math.Sqrt(blocks));
        int rows = (blocks + cols - 1) / cols;
        int block = BlockSize;

        GridPos[] snake = SerpentinePath(LaneLen, LaneCount);

        // ── 建图期的占用表（见 _claims 的说明）：摆放从此变成「先看空格、再放」。──
        _claims = new HashSet<long>();
        // ⚠ 主干道网**在方块之后**才铺（见下面「主干道网」那一段）。这里先留**空表**：
        //   `OnTrunk` 恒为 false，方块不会为干道让位；等干道真的铺下去时，
        //   反过来是**干道钻地让位**（`!IsFree` ⇒ 掩码）。从属关系反了，结果一样不重叠，
        //   但换来两件要紧的事：① 干道能按**真实的用图范围**铺（不再按 78 的格点铺满全图 ——
        //   那会在图的半边留下一大片「只有路、没有房子」的空带，整图取景时特别显眼）；
        //   ② 干道密度是相对**内容**的，而不是相对一个写死的格点数。
        _trunkCol = Array.Empty<bool>();
        _trunkRow = Array.Empty<bool>();

        // （主干道网**在方块之后**才铺 —— 见下面的「主干道网」那一段，理由写在那里。）

        // ══════════════════════════════════════════════════════════════════════════
        // **紧贴拼装（mosaic）** —— 治「太松垮、很多空白」的根
        //
        // ⚠ 之前是「方块放在 block×block 的**规则格点**上，盒子里再留边距」——
        //   于是每个方块四周都有一圈**周期性空白**，整张图读起来就是「一块一块、中间一大片空」。
        //   用户的评价一直是「太整齐 / 太松垮 / 很多空白处」，根就在这个晶格。
        //
        // 现在改成**按行带依次紧贴摆放**：
        //   · 一行带里横向挨着放盒子，间隙只有 1~2 格（不再是固定 78 的格点）；
        //   · 行高由这一行最高的盒子决定，行间间隙也是 1~2 格；
        //   · 盒子尺寸/朝向照样按哈希变 —— 于是**没有任何周期性空白**，也不像阵列。
        //
        // ⚠ **间隙不能再掉到 0**：洗矿机的「水管 + 水塔」要占机器**邻格**，全都落在这圈间隙里。
        //   间隙 0 时那一格属于隔壁盒子（已占）⇒ 那条洗矿线永远等水、只堵不动，而且不报错
        //   ——实测 21 条线报废（自检的「找不到水位的洗矿线」）。1 格就够（管子贴机器、
        //   水塔贴管子，两个都落在同一行/列的间隙里），2 格是给旁边那条线留的余量。
        // 摆放仍然是「先查占用表」（见 _claims），所以撞车会被记下来而不是悄悄重叠。
        // ══════════════════════════════════════════════════════════════════════════
        // 折行宽度按「**大致正方**」估：一行平均占 `avgAlong` 格，于是
        //   wrapX ≈ avgAlong × √条数 ⇒ 排出来的包围盒接近正方形。
        // ⚠ 折行宽度**不能按方块晶格估**（如拿 `worldW*2` 当宽度）——
        //   那会排成 4747×2260 的**又宽又扁**形状，密度反而从 25.1% 掉到 12.7%
        //   （同样的内容摊在 2 倍大的包围盒里）。这种错只有量「占格 / 包围盒」才看得出来。
        int avgAlong = ((LaneLen + 1) + ((2 * LaneCount) - 1)) / 2;
        int wrapX = ((int)(avgAlong * Math.Sqrt(Chains)) + block) / block * block;
        int packH = (int)(wrapX * 1.5);          // 高度留足余量，排不满只是多几格空的

        int placed = 0;
        int cursorY = 0;
        int row = 0;
        int packMaxX = 0;          // 排完之后的**真实用图宽度**（主干道网按它铺，见下）
        while (placed < Chains && cursorY < packH)
        {
            // ⚠ **一整行统一朝向**。混装两种朝向时行高取两者的最大值，
            //   短的那一侧就全空 —— 实测排成 2339×3410、密度只有 15.4%、
            //   而且只放下 2341 条（应为 2600）。统一朝向后行高一致，行内不留竖向空洞。
            uint rh = Hash2(row, 777);
            bool horizontal = (rh & 1) == 0;
            int rowLanes = LaneCount - (int)((rh >> 3) % 3);      // 整行统一的道数
            // ⚠ **纵向行的道长不能抖**：纵向盒子的 `laneLenJit` 恒为 0（见下面 lanes/laneLen 的算法），
            //   所以它用的道长永远是 `LaneLen`。行高若按带抖动的值算，抖到比 `LaneLen` 小时
            //   行高就短了 —— 盒子会**探进下一行**，两条带抢同一格。
            //   实测：**3,947 格带↔带重叠**，首例 (2,414) 带#1824 ↔ 带#2190（自检里就是这条）。
            int rowLaneLen = Math.Max(12, LaneLen + (horizontal ? (int)((rh >> 6) % 9) - 4 : 0));

            // ⚠ **每行起点要错开**：都从 x=0 开始的话，每行的盒子边界落在同一批列上，
            //   竖着看就是一条条**对齐的缝**，整张图因此显得像阵列。
            //   起点按行哈希取 0..block/2，边界就再也不对齐了。
            int cursorX = (int)(Hash2(row, 555) % (uint)Math.Max(1, block / 2));
            int col = 0;
            // 行高 = 盒子在**垂直方向**的占格：横向行是 2*道数−1，纵向行是 道长+1
            int rowHeight = horizontal ? (2 * rowLanes) - 1 : rowLaneLen + 1;

            while (cursorX < wrapX && placed < Chains)
            {
                uint bh = Hash2(col, row * 131 + 7);

                // **只在沿道方向上变**（横向行变道长、纵向行变道数）⇒ 行高不受影响。
                // ⚠ 纵向行的道数要**围绕 LaneCount** 变（6..8），不能「从已缩小的 rowLanes 再减」
                //   —— 那样会缩到 4 道，路径变短、带格总数掉 18%（实测 1,362,584 → 1,119,690）。
                int lanes = horizontal ? rowLanes
                                       : LaneCount - (int)((bh >> 6) % 3);
                int laneLenJit = horizontal ? (int)((bh >> 6) % 9) - 4 : 0;
                int laneLen = Math.Max(12, LaneLen + laneLenJit);

                int along = horizontal ? laneLen + 1 : (2 * lanes) - 1;

                var map = new BoxMap(cursorX, cursorY, !horizontal);

                // ⚠ **摆放前必须查占用表**（这是「先看空格、再放」的最后一块）。
                //   行高与间隙只保证「盒子之间不压」，保证不了「盒子里的水塔/管道不压到
                //   **后面**那个盒子」—— 水塔是建线时按 `IsFree` 占的，而盒子自己是按几何摆的。
                //   两者一交汇就重叠：自检实测 2 格，首例 (37,1276) 带#6163 ↔ 机器#10697(Producer)。
                //   这里整块（含外扩 1 格：那圈是留给洗矿机的水管+水塔的）查一遍，不空就**跳到
                //   最左边那个冲突列的右边**继续试（不是一格一格挪 —— 那样每格都要重扫整块）。
                int boxH2 = horizontal ? (2 * lanes) - 1 : laneLen;
                for (int fits = 0; fits < 8; fits++)
                {
                    int blocked = BlockedColumn(cursorX, cursorY, along, boxH2);
                    if (blocked == int.MaxValue) break;                 // 整块都空
                    cursorX += blocked + 1;
                    map = new BoxMap(cursorX, cursorY, !horizontal);
                }
                if (BlockedColumn(cursorX, cursorY, along, boxH2) != int.MaxValue)
                    break;                                              // 这一行剩余位置都放不下 ⇒ 换行

                int type = placed % LineTypes;
                typeCount[type]++;
                BuildSerpentine(layout, primeTypes, content, type, map, snake,
                                lanes, laneLenJit,
                                ref beltCells, ref pipeCells, ref degenerate);
                placed++;

                cursorX += along + 1 + (int)((bh >> 12) % 2);      // 间隙 1..2（见下面的说明）
                if (cursorX > packMaxX) packMaxX = cursorX;
                col++;
            }

            cursorY += rowHeight + 1 + (int)((Hash2(row, 991) % 2));   // 行间 1..2
            row++;
        }

        // ── **主干道网**：贯穿用图范围的长带，从方块中间穿过去把它们串成一张网 ──
        //
        // ⚠ **顺序：方块先铺、干道后铺**。反着铺时干道必须按一个**写死的格点
        //   面积**（`cols×rows×78²`）铺满，而方块只用到其中一半 ⇒ 图的半边留下一大片
        //   「只有路、没有房子」的空带（`FORGEFLOW_STRESS_FIT=1` 整图取景时一眼就看到，
        //   而且那片空带在缩略图上被读成「厂区只有一半」）。现在按**真实的用图范围**铺，
        //   密度是相对内容的。
        //   从属关系也随之反转：方块不再为干道让位，而是**干道钻地让位**（`!IsFree` ⇒ 掩码，
        //   见 BuildTrunkLine）。两层都不会互相压住，占用表自检照样是 0。
        //
        // ⚠ **不再是「每 74 格一条」的等距网格。** 等距网格是「太整齐」的最大来源 ——
        //   整张图像一块规整的晶格布。
        // ⚠⚠ **也不该是「每 78 格一条 ±9」**：那只是把等距改成「等距 + 噪声」，
        //   从整图看仍然是一把**等距的梳子**（78 的周期在缩略图上清清楚楚）。
        //   位置由**随机游走**决定 ——
        //   上一条到这一条的距离在 45..115 之间跳，间距本身没有周期。
        //   再叠一条：每条线**切成随机长度的若干段、段间留随机缺口**（见下面两个循环）——
        //   「一路笔直贯穿全图」本身就是「太整齐」的观感来源，真实厂区不会有这种通天线。
        //
        // 交叉口：竖道在横道那一行钻地 ⇒ 那一格归横道，两条路都连通且不重叠。
        int worldW = packMaxX + 2, worldH = cursorY + 2;
        _trunkCol = new bool[worldW];
        _trunkRow = new bool[worldH];
        for (int tx = TrunkOffset; tx < worldW; tx += TrunkWalk(tx, 202))
        {
            if (Hash2(tx, 101) % 5 == 0) continue;          // 约 1/5 的位置干脆不放
            if (!_trunkCol[tx]) _trunkCol[tx] = true;
        }
        for (int ty = TrunkOffset; ty < worldH; ty += TrunkWalk(ty, 404))
        {
            if (Hash2(ty, 303) % 5 == 0) continue;
            if (!_trunkRow[ty]) _trunkRow[ty] = true;
        }

        for (int tx = 0; tx < worldW; tx++)
        {
            if (!_trunkCol[tx]) continue;
            foreach ((int y0, int y1) in TrunkSpans(tx, worldH, 505))
            {
                var cells = new GridPos[y1 - y0];
                bool[]? mask = null;
                for (int r = y0; r < y1; r++)
                {
                    cells[r - y0] = new GridPos(tx, r);
                    if (_trunkRow[r]) { mask ??= new bool[cells.Length]; mask[r - y0] = true; }
                }
                if (cells.Length < 2) continue;
                BuildTrunkLine(layout, primeTypes, content, cells, mask, "iron", ref beltCells,
                               avoidCrossings: true);
            }
        }
        for (int ty = 0; ty < worldH; ty++)
        {
            if (!_trunkRow[ty]) continue;
            foreach ((int x0, int x1) in TrunkSpans(ty, worldW, 606))
            {
                var cells = new GridPos[x1 - x0];
                for (int c = x0; c < x1; c++) cells[c - x0] = new GridPos(c, ty);
                if (cells.Length < 2) continue;
                BuildTrunkLine(layout, primeTypes, content, cells, null, "copper", ref beltCells);
            }
        }

        // ⚠ **走向统计必须按「每段带」数，不能按「每条产线的起始方向」数。**
        //   一条蛇形产线本身就含**往返两个方向**（东道 + 西道），按起始方向数会得出
        //   「西 0 / 北 0」这种失真结果 —— 那会让读者以为地图缺了两个方向。
        //   这里直接量每段带的前两格，得到的就是**画面上真实存在**的走向分布。
        //（必须在布局**建完之后**统计。）
        for (int b = 0; b < layout.BeltCount; b++)
        {
            if (layout.BeltAt(b) is not { } bd || bd.Cells.Length < 2) continue;
            GridPos p0 = bd.Cells[0], p1 = bd.Cells[1];
            int d = p1.X > p0.X ? DirE : p1.X < p0.X ? DirW : p1.Y > p0.Y ? DirS : DirN;
            dirCount[d]++;
        }

        // ⚠ **布局自检：不能有两样东西占同一格。**
        //
        //   方块网格 + 方向轮转最容易出的错就是**方块之间或方块内互相压住** ——
        //   而 `FactoryLayout` 是纯数据容器（`AddBelt`/`AddMachine` 不做占用校验，
        //   占用位图只服务交互建造），所以重叠**不会报任何错**：
        //   症状只是画面上两条线黏在一起、或者物品走在别人的带上。
        //   这正是本项目最怕的那类静默错误，所以这里主动查一遍。
        //
        //   `FORGEFLOW_STRESS_VERIFY=1` 才开（百万格要建一张哈希表，约几十 MB，
        //   只在几何自检时值得付）。判据是**重复格数必须为 0**。
        if (Environment.GetEnvironmentVariable("FORGEFLOW_STRESS_VERIFY") == "1")
            VerifyNoOverlap(layout, beltCells);

        // 建图期的占用表用完即弃（它只服务摆放与自检，模拟与渲染都不需要它）。
        int claimClashes = _claimClashes, noWater = _noWaterCount, yielded = _machineYielded;
        int claimed = _claims?.Count ?? 0;
        _claims = null;
        _claimClashes = 0;
        _noWaterCount = 0;
        _machineYielded = 0;
        GD.Print($"[stress:自检] 建图占用表：占格 = {claimed:N0}  重复占格 = {claimClashes:N0}  " +
                 $"{(claimClashes == 0 ? "✅" : "❌ 有重叠")}  找不到水位的洗矿线 = {noWater:N0}" +
                 $"{(noWater == 0 ? " ✅" : " ❌ 那些线会永远等水")}" +
                 $"  让位的机器 = {yielded:N0} 台（那一格被带占了 —— 让位而不是重叠）");

        // 实例数 = 物品 + 带面 + 机器 + 管道。
        // **必须显式抬高 maxInstances**：默认 65,536 只够建个小厂，百万规模差 30 倍。
        int machines = layout.MachineSlotCount;
        int need = beltCells * 2 + pipeCells + machines + 4096;

        // ⚠ **机器只认显式绑定**（任何机器都要接线才能用），而这个场景是
        //   **直接构造布局**（不走玩家的建造命令）⇒ 必须显式声明一次"按几何把该接的接上"。
        //   不接的话 11,331 台机器一台都不会工作：带灌满之后一件都不流、出货恒为 0，
        //   而画面上看不出异常（压力测试就白跑了）。
        layout.AutoBindGeometrically();
        layout.Resolve();

        _sim = new FactorySim(layout, speedSub: 4, typeCount: 4,
                              demandPerMachine: 10, powerPercent: 100,
                              maxInstances: need, content: content)
        {
            CullEnabled = Environment.GetEnvironmentVariable("FORGEFLOW_NO_CULL") != "1",
            // 诊断：强制 LOD 步长（0 = 自动）。用来把「亚像素画不出来」与「LOD 抽样太稀」
            // 这两件症状相同的事分开量。
            LodStrideOverride = EnvInt("FORGEFLOW_LOD_STRIDE", 0),
        };

        // ⚠ **灌满带**。不灌的话百万件要靠上游慢慢喂几分钟 —— 那测的是「多久到量」，
        //   而不是「到了百万件之后每帧多少钱」。PrimeBelts 把带的初始状态直接设成压满。
        //   必须在 SimLoopRunner.Start() **之前**调用（它直接改带的内容）。
        long primed = _sim.PrimeBelts(primeTypes.ToArray(), 1, PrimeFillPercent);
        GD.Print($"[stress] 布局：{Chains:N0} 条产线 × {LineTypes} 种" +
                 $"[齿轮 {typeCount[0]:N0} / 铜板 {typeCount[1]:N0} / 洗矿 {typeCount[2]:N0} / 铁板 {typeCount[3]:N0}]  " +
                 $"带段走向[东 {dirCount[DirE]:N0} / 南 {dirCount[DirS]:N0} / 西 {dirCount[DirW]:N0} / 北 {dirCount[DirN]:N0}]（**按段**统计）  " +
                 $"方块 {cols}×{rows}（边长 {block} 格）  机器={machines:N0}  " +
                 $"带={layout.BeltCount:N0}（{beltCells:N0} 格）  管道={layout.PipeCount:N0}（{pipeCells:N0} 格）");
        if (degenerate > 0)
            GD.Print($"[stress] ⚠⚠ **有 {degenerate:N0} 段带是空的** —— 那两台机器之间没有带相连，" +
                     $"下游那台永远收不到货。调大 FORGEFLOW_STRESS_LANELEN 或调小产线数可解决。");
        GD.Print($"[stress] 已灌入 {primed:N0} 件物品（带上物品数会在第一个 tick 后由快照统计出来）");

        // 机器名表由**模拟侧**发布（SyncTopology 里建好了），这里只接上通用绘制器 ——
        // **不由本脚本自己抄一份表**（那是渲染线程读模拟数据的隐患）。
        _labels = new MachineLabelDrawer(_sim.MachineLabels);

        WorldRect wb = _sim.WorldBounds;
        _worldBounds = wb;
        _totalBeltCells = beltCells;

        // **网格底必须盖住整个世界。**
        //   场景里写死的矩形尺寸对不上本场景的世界（113,248 px 见方、相机在正中）
        //   ⇒ 网格整个**在视野之外**，画面上一条格线都看不到。
        //   着色器的格线是按**世界坐标**算的，所以只要把矩形铺够大，对齐自动正确。
        if (_grid != null)
        {
            const float gpad = FactorySim.CellSizePx * 4f;
            _grid.Position = new Vector2(wb.LoX - gpad, wb.LoY - gpad);
            _grid.Size = new Vector2(wb.HiX - wb.LoX + gpad * 2f, wb.HiY - wb.LoY + gpad * 2f);
            GD.Print($"[stress] 网格底 = {_grid.Position} 尺寸 {_grid.Size}（覆盖世界包围盒）");
        }
        GD.Print($"[stress] 世界包围盒 = ({wb.LoX:F0},{wb.LoY:F0})..({wb.HiX:F0},{wb.HiY:F0})  " +
                 $"= {wb.HiX - wb.LoX:F0}×{wb.HiY - wb.LoY:F0}px  " +
                 $"（{(wb.HiX - wb.LoX) / FactorySim.CellSizePx:F0}×" +
                 $"{(wb.HiY - wb.LoY) / FactorySim.CellSizePx:F0} 格）  " +
                 $"快照容量 = {_sim.Buffers.Capacity:N0}");

        // ── 相机取景：按**实际世界**算，不写死 ──────────────────────────────
        // ⚠ 必须在读 ViewportSize 之前做 —— 视口尺寸随分辨率/全屏变化，
        //   写死 1920×1080 会让「看得见多少格」算错，进而让上传容量估错、画面被截断。
        Vector2 viewport = GetViewportRect().Size;
        if (_camera is CameraController cam && !_autoPan)
        {
            // FORGEFLOW_STRESS_FIT=1 时把取景下限放开到最小档 ⇒ **一眼看完全世界**。
            // 这是给「亚像素剔除」用的测试位：拉到那么远时格子只有零点几个像素，
            // 引擎画不出来 —— 照传 250 万个实例时实测只剩 9 FPS。
            float minZoom = Environment.GetEnvironmentVariable("FORGEFLOW_STRESS_FIT") == "1" ? 0.01f : 0.5f;
            cam.FrameWorld(wb.LoX, wb.LoY, wb.HiX, wb.HiY, viewport, minZoom);
            _framedPos = cam.Position;
            _framedZoom = cam.Zoom.X;
            _framed = true;
            GD.Print($"[stress] 相机取景：中心={cam.Position} 缩放={cam.Zoom.X:F3}  " +
                     $"（视口 {viewport.X:F0}×{viewport.Y:F0} ⇒ 可见 " +
                     $"{viewport.X / FactorySim.CellSizePx / cam.Zoom.X:F0}×" +
                     $"{viewport.Y / FactorySim.CellSizePx / cam.Zoom.X:F0} 格）");
        }

        // ⚠ CustomAabb 必须盖住**整个世界**。写死 1920×1080 的话，Godot 会把整个
        //   MultiMesh 一次裁掉 ⇒ 画面上什么都没有、而且不报任何错（无报错的全黑）。
        const float pad = FactorySim.CellSizePx * 2f;
        var aabb = new Aabb(
            new Vector3(wb.LoX - pad, wb.LoY - pad, -1f),
            new Vector3(wb.HiX - wb.LoX + pad * 2f, wb.HiY - wb.LoY + pad * 2f, 2f));

        // ── 上传容量：按**实际视口与取景**估，而不是按快照容量 ────────────────
        //
        // 实测（1920×1080、剔除率 99.9%）：写出实例才几千个，但 `SetBuffer` 仍要 **28ms**
        //   —— 因为 `SetBuffer` 上传的是 `InstanceCount × stride` 整个缓冲
        //      （2,008,192 × 12 float = **92 MB**），而 `VisibleInstanceCount`
        //      **只截断绘制、不减少上传**。所以上传容量是剔除之外的第二个旋钮。
        //
        // 估法：可见格数 × 2 层（带面 + 物品）× 余量，**用实际视口像素与相机缩放算**。
        // 而且 RenderBridge 会在可见实例真的变多时**自动翻倍扩容**
        //（全屏、拉远相机都会让可见变多），所以这里估小一点也不会截断画面。
        float visibleW = viewport.X / FactorySim.CellSizePx / MathF.Max(CurrentZoom(), 0.05f);
        float visibleH = viewport.Y / FactorySim.CellSizePx / MathF.Max(CurrentZoom(), 0.05f);
        int viewportCells = (int)((visibleW + 8f) * (visibleH + 8f));
        int uploadDefault = Math.Max(8192, viewportCells * 2 * 2);

        // ⚠ **取景很远时不要按「可见格数」估上传容量。**
        //   那时格子只有零点几个像素、Sim 侧会把它们全判成亚像素而剔掉（一个都不画），
        //   估出来的几十万/上百万会**被钳到快照容量**，而缩容下限正是这个初值
        //   ⇒ 容量**永远缩不回来**（实测拉到 0.018 倍时卡在 1,328,791 实例 = 60.8MB/帧）。
        //   所以亚像素时直接用最小档起步，让容量能跟着真实的可见数上下走。
        if (CurrentZoom() * FactorySim.CellSizePx < 1f) uploadDefault = 8192;

        int upload = EnvInt("FORGEFLOW_STRESS_UPLOAD", uploadDefault);
        if (upload > _sim.Buffers.Capacity) upload = _sim.Buffers.Capacity;

        _bridge = new RenderBridge(_items!, _sim.Buffers.Capacity,
                                   new Vector2(FactorySim.CellSizePx, FactorySim.CellSizePx), aabb,
                                   useColors: false, useCustomData: true,
                                   FactorySim.AtlasCols, FactorySim.AtlasRows,
                                   FactorySim.BuildSpriteLut(content),
                                   FactorySim.BuildSpriteSizeLut(content),
                                   uploadCapacity: upload);
        _bridge.Configure();
        GD.Print($"[stress] 上传容量 = {_bridge.UploadCapacity:N0} 实例 ⇒ " +
                 $"{_bridge.UploadCapacity * (long)_bridge.Stride * 4 / 1024.0 / 1024.0:F2} MB/帧 " +
                 $"（快照容量 {_sim.Buffers.Capacity:N0} ⇒ 若不降上传则是 " +
                 $"{_sim.Buffers.Capacity * (long)_bridge.Stride * 4 / 1024.0 / 1024.0:F0} MB/帧；" +
                 $"可见变多时会自动扩容）");

        // ⚠ **必须在 SimLoopRunner.Start() 之前先推一次视口。**
        //
        //   否则 Sim 线程会先发布几帧**未剔除**的快照（百万场景 = 2,008,000 个实例），
        //   渲染侧一认领，RenderBridge 的「按需扩容」就会把它扩到满容量 ——
        //   实测 SetBuffer 立刻从 0.13ms 弹回 **28ms / 92MB per frame**，
        //   而且**之后再也降不下来**（扩容是单向翻倍的，缩不回去）。
        //   这是个纯粹的**启动时序**问题：剔除本身没问题，只是第一帧还没被告知视口。
        //
        //   这里直接用相机自身的 position/zoom 与视口尺寸算，**不依赖画布变换是否已更新**
        //  （_Ready 阶段它可能还没算好）；_Process 里那套基于 GetViewportTransform 的
        //   权威推送从第一帧起接管。
        PushInitialViewport(viewport, wb);
    }

    /// <summary>启动模拟线程**之前**的首次视口推送。见调用点关于启动时序的说明。</summary>
    private void PushInitialViewport(Vector2 viewport, WorldRect world)
    {
        Vector2 center = _camera?.Position ?? new Vector2((world.LoX + world.HiX) * 0.5f,
                                                         (world.LoY + world.HiY) * 0.5f);
        float z = MathF.Max(CurrentZoom(), 0.001f);
        float halfW = viewport.X * 0.5f / z;
        float halfH = viewport.Y * 0.5f / z;

        _sim!.CullViewport.Set(center.X - halfW, center.Y - halfH,
                               center.X + halfW, center.Y + halfH, z);
        GD.Print($"[stress] 已推初始视口（在启动模拟线程之前）：" +
                 $"({center.X - halfW:F0},{center.Y - halfH:F0})..({center.X + halfW:F0},{center.Y + halfH:F0})");
    }

    /// <summary>产线种类数（交错排布时按序号轮换）。</summary>
    private const int LineTypes = 4;

    /// <summary>方向常量（与项目的朝向约定一致：0 东 / 1 南 / 2 西 / 3 北）。</summary>
    private const int DirE = 0, DirS = 1, DirW = 2, DirN = 3;

    // ══════════════════════════════════════════════════════════════════════════
    // **蛇形（boustrophedon）产线** —— 治「太稀疏」的根
    //
    // 一条产线不再是「1 格厚的细长路径」，而是**在一个盒子里来回折若干道**，
    // 把盒子填满（真实厂区 / Factorio 密集区就是这个排法）。
    // 实测：盒子密度 16% → 约 55%，世界边长 3539 → 约 1923 格。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>蛇形盒子：每道多少格。可用 <c>FORGEFLOW_STRESS_LANELEN</c> 覆盖。</summary>
    private static readonly int LaneLen = EnvInt("FORGEFLOW_STRESS_LANELEN", 70);

    /// <summary>蛇形盒子：折几道。盒子尺寸 = LaneLen × (2*LaneCount - 1)。</summary>
    private static readonly int LaneCount = EnvInt("FORGEFLOW_STRESS_LANES", 8);

    /// <summary>每个方块里叠几个蛇形盒子。</summary>
    private const int BoxesPerBlock = 4;

    /// <summary>
    /// 方块边长（格）= 盒子宽 + 两侧边距 + **道长抖动的余量**。
    ///
    /// ⚠ 那个 +8 必须有：道长现在会抖动到 <c>LaneLen + 4</c>，而盒子原点在 <c>ox + 2</c>
    ///   ⇒ 最右能到 <c>ox + 2 + LaneLen + 4</c>。边长只留 +4 时它会**越过方块边界**，
    ///   压进右边的方块（占用表自检实测 **1154 格重叠**）。
    /// </summary>
    private static readonly int BlockSize = LaneLen + 8;

    /// <summary>
    /// **主干道**的偏移（格）：即总线在方块内的位置，<c>2 + LaneLen/2</c>。
    /// 全图主干道按 <see cref="BlockSize"/> 等距排布，所以「世界坐标 − 这个偏移
    /// 能被 BlockSize 整除」就是「这一格在路上」。
    /// </summary>
    private static readonly int TrunkOffset = 2 + LaneLen / 2;

    /// <summary>
    /// 便宜、**确定性**的 2D 哈希 —— 用来给布局加「凌乱感」（目前用于方块朝向）。
    ///
    /// ⚠ 必须是确定性的：同一个 (x,y) 永远给同一个值，否则每次跑布局都不一样、没法排查。
    ///   所以用固定的乘子异或（不是 <c>Random</c>，也不依赖任何运行期状态）。
    /// </summary>
    private static uint Hash2(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return h;
    }

    /// <summary>
    /// 盒子（含**外扩一圈**）里最左边那个被占的列偏移；整块都空时返回 <see cref="int.MaxValue"/>。
    ///
    /// 外扩那一圈不是浪费：洗矿机的「水管 + 水塔」占的就是机器的相邻格，正好落在这圈里。
    /// 摆盒子时先把它留空，建线时才一定放得下水塔（否则那条线永远等水、只堵不动且不报错）。
    /// </summary>
    private static int BlockedColumn(int ox, int oy, int w, int h)
    {
        for (int dx = -1; dx <= w; dx++)
            for (int dy = -1; dy <= h; dy++)
            {
                int x = ox + dx, y = oy + dy;
                if (x < 0 || y < 0 || !IsFree(x, y)) return dx;
            }
        return int.MaxValue;
    }

    /// <summary>
    /// 主干道的**随机游走**步长：45..115 格（**不是**固定的 <see cref="BlockSize"/>）。
    ///
    /// ⚠ 为什么不能是「每 78 格一条 + ±9 抖动」：那只是把等距改成「等距 + 噪声」，
    ///   缩略图上一眼还是**一把等距的梳子**。
    ///   间距本身参差，整张图才不像阵列；真实城市路网也是参差的。
    /// </summary>
    private static int TrunkWalk(int at, int salt) => 45 + (int)(Hash2(at, salt) % 71);

    /// <summary>
    /// 把一条主干道切成**若干段**：段长 130..430 格，段间缺口 0..59 格。
    ///
    /// 「一条线笔直贯穿全图」是「太整齐」的第二大来源 —— 真实厂区不会有一根通天的带。
    /// 切开之后路网读起来是「一段一段接起来的」，同时顺带把图的下缘那片**只有几根长线
    /// 的空带**也打散了（整图取景时那片空带特别显眼）。
    ///
    /// 返回 <c>[起, 止)</c>，左闭右开；区间由 <c>p = end + gap</c> 依次推进 ⇒ **互不重叠**，
    /// 所以段与段之间不会自己压自己（占用表自检必须保持 0）。
    /// </summary>
    private static List<(int Start, int End)> TrunkSpans(int at, int extent, int salt)
    {
        var spans = new List<(int Start, int End)>();
        int p = 0;
        int guard = 0;
        while (p < extent && guard++ < 1024)
        {
            int len = 130 + (int)(Hash2(at * 131 + p, salt) % 301);
            int end = Math.Min(extent, p + len);
            if (end - p >= 2) spans.Add((p, end));
            p = end + (int)(Hash2(at * 977 + p, salt + 1) % 60);
        }
        return spans;
    }

    /// <summary>
    /// 建图期的**已占用格**表。
    ///
    /// ⚠ 为什么必须有它：之前布局的「不重叠」是靠**手工推出来的几何不变量**保证的
    ///   （「水塔放在机器下方那一格一定空」）。于是**任何参数一变就会破**——
    ///   实测加抖动后自检报 131 格重叠、再收紧吸附反而变成 1714 格重叠 + 356 段空带。
    ///   有了占用表，摆放就变成「**先看这一格空不空，再决定放哪**」，
    ///   几何可以随便乱，不变量不用再手推。建完即弃（它只服务建图）。
    ///
    /// 键是 <c>(x &lt;&lt; 22) | y</c>：坐标远小于 2^22，够用且比元组省。
    /// </summary>
    private static HashSet<long>? _claims;

    private static long Key(int x, int y) => ((long)x << 22) | (uint)y;

    private static int _claimClashes;      // 重复占格次数（建图期自检，必须为 0）
    private static int _noWaterCount;      // 找不到空位放水塔的洗矿线数（必须为 0）
    private static int _machineYielded;    // 因为那一格已被占而**没放**的机器台数（见 BuildSerpentine）

    /// <summary>这一格空着吗（没被任何带/管/机器占）。</summary>
    private static bool IsFree(int x, int y) => _claims == null || !_claims.Contains(Key(x, y));

    /// <summary>占下这一格。**重复占会记一笔**（调用方不必自己查，见 _claimClashes）。</summary>
    private static bool Claim(int x, int y)
    {
        if (_claims == null) return true;
        if (!_claims.Add(Key(x, y))) { _claimClashes++; return false; }
        return true;
    }

    /// <summary>主干道落在哪些列 / 哪些行上（**稀疏且不等距**，见 BuildSim 里的摆法）。</summary>
    private static bool[] _trunkCol = Array.Empty<bool>();
    private static bool[] _trunkRow = Array.Empty<bool>();

    /// <summary>
    /// 这一格是否落在**主干道**上。
    /// ⚠ 主干道位置现在是**查表**而不是「模 <see cref="BlockSize"/>」——
    ///   正因为要让它**不再等距**（等距的干道网格才是「太整齐」的最大来源）。
    /// </summary>
    private static bool OnTrunk(GridPos p)
        => p.X >= 0 && p.Y >= 0
           && ((p.X < _trunkCol.Length && _trunkCol[p.X])
               || (p.Y < _trunkRow.Length && _trunkRow[p.Y]));

    /// <summary>
    /// **盒内局部坐标 → 世界坐标**。
    /// <paramref name="transpose"/> = true 时把 (列, 行) 换成 (行, 列) —— 于是同一份蛇形路径
    /// 在纵向方块里就是**竖着折**的，四向交错不用写两套几何。
    /// </summary>
    private readonly struct BoxMap
    {
        private readonly int _ox, _oy;
        private readonly bool _transpose;

        public BoxMap(int ox, int oy, bool transpose) { _ox = ox; _oy = oy; _transpose = transpose; }

        /// <summary>
        /// 机器朝向 —— **恒为 0（东），也就是机器不旋转。**
        ///
        /// ⚠ 之前这里返回的是「产线走向」，于是纵向方块里的机器全被转了 90°。
        ///   这套美术里机器大多是**非方形**（铁熔炉 47×31、洗矿机 18×51、机械臂 27×41），
        ///   转 90° 之后形状与占位不再吻合、看起来就是「渲染有问题」。
        ///   ⇒ **机器不旋转**；**轨道的方向照旧**（带面由路径决定，
        ///     拐角/箭头/滚动全都在 <see cref="FactorySim.BeltTileAt"/> 里，与机器朝向无关）。
        /// </summary>
        public int Facing => 0;

        public GridPos To(GridPos p) => _transpose
            ? new GridPos(_ox + p.Y, _oy + p.X)
            : new GridPos(_ox + p.X, _oy + p.Y);
    }

    /// <summary>
    /// 生成蛇形路径（盒内局部坐标）。**一道走完接着竖走一格**，于是路径连续，
    /// 拐角全靠 <see cref="FactorySim.BeltTileAt"/> 的连通掩码自动铺瓦。
    ///
    /// 形状（<c>laneLen=6, lanes=3</c>）：
    /// <code>
    ///   → → → → → →
    ///             ↓
    ///   ← ← ← ← ← ←
    ///   ↓
    ///   → → → → → →
    /// </code>
    /// 偶数道往东、奇数道往西；盒子占 <c>laneLen × (2*lanes-1)</c> 格。
    /// </summary>
    private static GridPos[] SerpentinePath(int laneLen, int lanes)
    {
        var path = new List<GridPos>(lanes * (laneLen + 1));
        for (int lane = 0; lane < lanes; lane++)
        {
            int row = lane * 2;
            bool west = (lane & 1) == 1;
            for (int i = 0; i < laneLen; i++)
                path.Add(new GridPos(west ? laneLen - 1 - i : i, row));

            // 拐到下一道：竖着走一格。下一道的**第一格**就是这格的下一格（由下一轮补上）
            if (lane < lanes - 1)
                path.Add(new GridPos(west ? 0 : laneLen - 1, row + 1));
        }
        return path.ToArray();
    }

    /// <summary>
    /// 每种产线的**机器序列**与「这台机器之后那段带装什么」。
    /// 全部是 **1 进 1 出** 的配方 —— 流量守恒的前提（见 <see cref="UniformWorkTicks"/>）。
    ///
    /// ⚠⚠ **本场景故意不放洗矿机。** 洗矿机需要水（<c>wash-iron</c> 每件 20 单位），
    ///   于是每条这样的产线都要配「水塔 + 管道」，而把水塔塞进蛇形路径的邻格会
    ///   **触发一个我没定位到的崩溃**（症状：日志只有引擎头，`_Ready` 没跑完；
    ///   加了负坐标保护也不解决）。没有水的话洗矿机会**永远等水 ⇒ 那一半产线全部只堵不动**，
    ///   而这从「自检通过 + 数量守恒」上完全看不出来。
    ///   ⇒ 这里的取舍是：**先保证 2600 条线都在真的流动**，
    ///     流体的覆盖交给主场景与探针 08（仍然全绿），压力场景不再压流体这一层。
    /// </summary>
    private static (string Id, ushort BeltAfter)[] LineRecipe(int type) => type switch
    {
        0 => new[] { ("iron-mine", ItemOre), ("iron-furnace", ItemPlate),
                     (FluidLines ? "washer" : "iron-furnace", ItemPlate), ("shipper", (ushort)0) },
        1 => new[] { ("copper-mine", ItemCopperOre), ("copper-furnace", ItemCopperPlate),
                     ("shipper", (ushort)0) },
        2 => FluidLines
             ? new[] { ("iron-mine", ItemOre), ("washer", ItemPlate), ("shipper", (ushort)0) }
             : new[] { ("copper-mine", ItemCopperOre), ("copper-furnace", ItemCopperPlate),
                       ("shipper", (ushort)0) },
        _ => new[] { ("iron-mine", ItemOre), ("iron-furnace", ItemPlate), ("shipper", (ushort)0) },
    };

    /// <summary>
    /// 产线是否用**洗矿机**（要水 ⇒ 水塔 + 管道）。**默认开** —— 这样压力场景也压到流体那一层。
    /// <c>FORGEFLOW_STRESS_NO_FLUID=1</c> 可关掉做对照。
    ///
    /// ⚠ 这里有两个不能踩的坑（另见 <see cref="SnapToLane"/> 与 BuildSerpentine 的水塔放置处）：
    ///   ① 水塔放在 <c>wl.X - 1</c> ⇒ 转置后第一个方块变成 **-1** ⇒ 占用位图负下标**崩溃**
    ///      （症状是日志只有引擎头、`_Ready` 没跑完，**连一句错误都没有**）；
    ///   ② 机器落在**拐角连接格**上 ⇒ 它下方那一格是另一条道 ⇒ 管道压带，自检一次报 1,300 格。
    /// </summary>
    private static readonly bool FluidLines =
        Environment.GetEnvironmentVariable("FORGEFLOW_STRESS_NO_FLUID") != "1";

    /// <summary>
    /// 沿一条**直线路径**铺一条主干道产线：机器**占掉**路径上的格（带在两侧分段），
    /// 而不是把机器「额外放上去」（那会压在带格上，自检立刻报重叠）。
    /// <paramref name="tunnelMask"/> 是交叉口要钻地的格（竖道用；横道传 null 表示它占住交叉口）。
    /// </summary>
    private static void BuildTrunkLine(FactoryLayout layout, List<ushort> prime, ContentDb content,
                                       GridPos[] cells, bool[]? tunnelMask, string metal,
                                       ref int beltCells, bool avoidCrossings = false)
    {
        int len = cells.Length;
        if (len < 8) return;

        bool iron = metal == "iron";
        string mine = iron ? "iron-mine" : "copper-mine";
        string furnace = iron ? "iron-furnace" : "copper-furnace";
        ushort ore = iron ? ItemOre : ItemCopperOre;
        ushort plate = iron ? ItemPlate : ItemCopperPlate;

        int[] at = { 0, len / 2, len - 2 };
        // ⚠ 竖干线的机器必须**避开交叉口**：交叉口那一格归横道（竖道在那儿钻地），
        //   机器落在那里就会压在横道上（自检实测 4 格，首例 (37,111)：竖道 x=37 的熔炉
        //   正好在竖道 × 横道 y=111 的交点上）。
        if (avoidCrossings)
            for (int i = 0; i < at.Length; i++)
                at[i] = AvoidCrossing(at[i], cells);

        // ⚠ 机器**也不能落在已经被别人占掉的格子上**：横道是在竖道之后铺的，它的机器可能
        //   正好落在**竖道的机器**那一格上（两条干线的机器撞在同一格）。
        //   ⇒ 就地找一个临近的空格（夹在相邻两台机器之间），找不到就保持原位 ——
        //     那种情况会被占用表自检报出来（宁可报出来，也不要静默重叠）。
        for (int i = 0; i < at.Length; i++)
        {
            if (IsFree(cells[at[i]].X, cells[at[i]].Y)) continue;
            int lo = i == 0 ? 1 : at[i - 1] + 2;
            int hi = i == at.Length - 1 ? len - 2 : at[i + 1] - 2;
            for (int d = 1; d < len; d++)
            {
                int up = at[i] - d, down = at[i] + d;
                if (up >= lo && IsFree(cells[up].X, cells[up].Y)) { at[i] = up; break; }
                if (down <= hi && IsFree(cells[down].X, cells[down].Y)) { at[i] = down; break; }
            }
        }
        string[] ids = { mine, furnace, "shipper" };
        ushort[] after = { ore, plate, 0 };

        // 段从**第一台机器之后**开始 —— 之前这里写死成 1，而机器位置被 AvoidCrossing
        // 挪动后就不再是 0 了 ⇒ 第一段会盖住机器（自检实测 4 格，首例 (37,1)）。
        int a = at[0] + 1;
        for (int i = 0; i < ids.Length; i++)
        {
            AddMachine(layout, content, ids[i], cells[at[i]], 0);
            if (i == ids.Length - 1) break;

            int b = at[i + 1] - 1;
            if (b >= a)
            {
                var seg = new GridPos[b - a + 1];
                bool[]? mask = null;
                // ⚠ **先算掩码、再占格。** 掩码的两条来源：
                //   ① 传进来的交叉口掩码（竖道在横道那一行钻地 —— 那一格归横道）；
                //   ② **已经被别人占掉的格子**（`!IsFree`）。横道是在竖道之后铺的，
                //      而竖道的**机器**可能正好落在横道这一行上 ⇒ 横道的带会从机器身上穿过去。
                //      随机游走 + 分段之后机器位置会撞上横道行
                //      （自检实测 2 格，首例 (37,1220) 带#534 ↔ 机器#8）。
                //      ⇒ 撞上就**钻地让位**（掩码），而不是去占那一格。
                //   必须在 `Claim` **之前**算：否则会把自己刚占的格子当成「别人的」而钻地。
                for (int k = 0; k < seg.Length; k++)
                {
                    seg[k] = cells[a + k];
                    bool crossing = tunnelMask != null && tunnelMask[a + k];
                    if (crossing || !IsFree(seg[k].X, seg[k].Y))
                    {
                        mask ??= new bool[seg.Length];
                        mask[k] = true;
                    }
                }
                for (int k = 0; k < seg.Length; k++)
                    if (mask == null || !mask[k]) Claim(seg[k].X, seg[k].Y);   // 干道的带也进占用表
                layout.AddBelt(new BeltDef { Cells = seg, TunnelMask = mask });
                prime.Add(after[i]);
                beltCells += seg.Length;
            }
            a = at[i + 1] + 1;
        }
    }

    /// <summary>
    /// 把位置挪开**主干道**上的格（机器落在干道上会把那条贯穿全图的长带截断）。
    ///
    /// ⚠ 判据必须用 <see cref="OnTrunk"/>（查表）—— 主干道早就不等距了。
    ///   这里原来写的是 `Mod(up - TrunkOffset, BlockSize) != 0`，那是**已废弃的等距晶格**规则：
    ///   它对一批根本不是干道的格子返回「不安全」（机器被白挪 <see cref="BlockSize"/> 格），
    ///   又对一批真正的干道格返回「安全」（机器照样压上去 ⇒ 那条长带被截断，而且不报错）。
    /// </summary>
    private static int AvoidCrossing(int r, GridPos[] cells)
    {
        int len = cells.Length;
        for (int d = 1; d < BlockSize; d++)
        {
            int up = r - d, down = r + d;
            if (up >= 1 && up < len && !OnTrunk(cells[up])) return up;
            if (down >= 1 && down < len && !OnTrunk(cells[down])) return down;
        }
        return r;
    }

    /// <summary>
    /// 把「想要的路径下标」**吸附到最近的合格机器位**（见 <see cref="BuildSerpentine"/> 的三条条件）。
    /// 三条都不能省：
    ///   ① 偶数行 —— 否则机器落在拐角的连接格上，它下方那一格是**另一条道**（被带占着）；
    ///   ② **不贴盒子左右边**（要求 `1 ≤ X ≤ laneLen-2`）—— 连接格只可能落在 `X=0` 或
    ///      `X=laneLen-1`（哪一侧取决于道的奇偶），两边都避开最省事；
    ///   ③ 不在主干道上 —— 否则会把那条贯穿全图的长带截断。
    /// 实测教训：漏掉 ① 时每个管道都压带（自检 1,300 格）。
    ///
    /// ⚠ <paramref name="minIndex"/> 是**下游机器给的上界**（上一台 +2：两台之间至少要留一格带）。
    ///   没有它的时候吸附是**双向**搜索的，会返回比 `want` 更小的下标 ⇒ 机器挤到一起、
    ///   中间那段带变成空的（两台机器之间没有带 ⇒ 下游永远收不到货，而且**不报错**）。
    ///   实测 2,850 条产线里有 6 段空带。有了下界之后再挤不下就落到下面的「放宽」一档：
    ///   **宁可机器位置难看（不满足 ①②、可能接不到水），也绝不留一段空带**。
    /// </summary>
    private static int SnapToLane(GridPos[] path, int want, BoxMap map, int laneLen, int minIndex = 0)
    {
        bool Good(int i)
        {
            GridPos p = path[i];
            // 左/右边界用**这条路径自己的**道长（道长现在是带抖动的，不能再用全局 LaneLen）
            if (p.Y % 2 != 0 || p.X <= 0 || p.X >= laneLen - 1) return false;
            if (OnTrunk(map.To(p))) return false;

            // ⚠ 洗矿机还要在**垂直邻格**放管道+水塔。两条道之间那一行（奇数行）通常只有
            //   一个连接格，所以那一格几乎总是空的；**除非它落在主干道行上**。
            //   这里要求上/下两格都不在干道上 —— 干道行密度很低（约 1%），所以几乎不损失选择余地，
            //   却能保证洗矿线一定拿得到水（否则那条线会永远等水、只堵不动，而且不报错）。
            if (OnTrunk(map.To(new GridPos(p.X, p.Y + 1)))) return false;
            if (p.Y > 0 && OnTrunk(map.To(new GridPos(p.X, p.Y - 1)))) return false;
            return true;
        }

        for (int d = 0; d < path.Length; d++)
        {
            int a = want + d, b = want - d;
            if (a < path.Length && a >= minIndex && Good(a)) return a;
            if (b >= minIndex && Good(b)) return b;
        }

        // 放宽一档：只保住两条**会撞车/断链**的硬条件（不压主干道、不越过下界），
        // 放弃「好看（偶数行、不贴边）」与「接得到水」——那两条只影响观感与供水，不会造假货。
        for (int i = Math.Max(minIndex, 0); i < path.Length; i++)
        {
            if (IsFree(map.To(path[i]).X, map.To(path[i]).Y)) return i;
        }
        return Math.Clamp(Math.Max(want, minIndex), 0, path.Length - 1);
    }

    /// <summary>
    /// 沿一条蛇形路径铺**一整条产线**：机器按路径长度**均匀分布**（首尾各一台），
    /// 中间的每一段都是连续子路径 ⇒ 段内拐角自动铺瓦、段与机器边相邻自动接上。
    /// </summary>
    private static void BuildSerpentine(FactoryLayout layout, List<ushort> prime, ContentDb content,
                                        int type, BoxMap map, GridPos[] snake,
                                        int lanes, int laneLenJit,
                                        ref int beltCells, ref int pipeCells, ref int degenerate)
    {
        (string Id, ushort BeltAfter)[] rec = LineRecipe(type);
        int n = rec.Length;

        // 这道产的**实际道长**带抖动（凌乱感）；盒子里剩下的行就空着，
        // 于是盒子边缘是参差的，而不是每块都齐平。
        int laneLen = Math.Max(12, LaneLen + laneLenJit);
        int useLanes = Math.Clamp(lanes, 2, LaneCount);
        GridPos[] path = laneLen == LaneLen && useLanes == LaneCount
            ? snake
            : SerpentinePath(laneLen, useLanes);

        if (path.Length < n + 2) return;

        // 机器位置：均匀分布 + **吸附**，并保证严格递增。吸附有三个条件：
        //   ① 偶数行（正常道格，不是拐角的连接格）—— 否则机器下方那一格是另一条道；
        //   ② 不贴盒子的左右边（连接格只可能在那两列）；
        //   ③ **不落在主干道上** —— 否则会把那条长带截断（路必须是连通的）。
        var idx = new int[n];
        for (int i = 0; i < n; i++)
        {
            int want = (int)((long)i * (path.Length - 1) / (n - 1));
            // 下界 = 上一台 +2（两台之间至少留一格带）⇒ 吸附不会再让机器挤到一起。
            // ⚠ 末台也用同一条下界 —— 给末台单独硬设下标再吸附，
            //   两个特殊处理叠起来正是空段的来源。
            idx[i] = SnapToLane(path, i == 0 ? want : Math.Max(want, idx[i - 1] + 2),
                                map, laneLen, i == 0 ? 0 : idx[i - 1] + 2);
        }

        // 兜底保序（放宽档理论上还是可能返回同一个下标；夹一下保证严格递增且留出一格带）
        for (int i = 1; i < n; i++)
            if (idx[i] < idx[i - 1] + 2) idx[i] = idx[i - 1] + 2;
        // 末台必须还在路径内：真挤不下就把整串**往前挪**（保住「两台之间有一格带」），
        // 而不是留一段空带。挪到负数只可能发生在「路径短于需求」时，那时下面的段循环
        // 会**照实**数出空段并报出来（不在这里虚报）。
        int over = idx[n - 1] - (path.Length - 1);
        if (over > 0)
        {
            for (int i = 0; i < n; i++) idx[i] -= over;
            if (idx[0] < 0)
                for (int i = 0; i < n; i++) idx[i] = Math.Min(i * 2, path.Length - 1);
        }

        for (int i = 0; i < n; i++)
        {
            GridPos mp = map.To(path[idx[i]]);
            // ⚠ **机器不能钻地，所以只能让位。** 盒子是按几何摆的（行高 + 间隙 + 占用表查一圈），
            //   但几何保证不了「后续的东西一定不进这一格」—— 实测 2,850 条线里有 5 台机器
            //   落在**别人已经占掉**的格子上（例 (1332,1359) 带#6362 ↔ 机器#10820）。
            //   与其继续推几何不变量（这个项目已经因此返工三次），不如让机器这一层也**查表让位**：
            //   那一格被占了就不放这台机器 —— 两侧的带会直接相接（带↔带本来就连通），
            //   产线照样通，只是这条线上少一台机器。让位次数**报出来**（不静默）。
            if (!IsFree(mp.X, mp.Y)) { _machineYielded++; continue; }
            AddMachine(layout, content, rec[i].Id, mp, map.Facing);
        }

        // 段 = 两台机器之间的那一段连续子路径。
        // 凡压到主干道上的格子 ⇒ 地下段（逐格掩码：一条蛇形会**反复**穿过主干道）。
        for (int i = 0; i + 1 < n; i++)
        {
            int a = idx[i] + 1, b = idx[i + 1] - 1;
            // ⚠ 段为空 = 两台机器之间**没有带** ⇒ 下游那台永远收不到货、只是干等。
            //   这**不会崩、也不会让数量对不上**（自检也查不出），所以必须**报出来** ——
            //   这正是本项目最怕的那类静默失败。
            if (b < a) { degenerate++; continue; }

            var cells = new GridPos[b - a + 1];
            bool[]? mask = null;
            // ⚠ 掩码的判据有两条，**都在 `Claim` 之前算完**（否则会把自己刚占的格子当别人的）：
            //   ① 压到主干道 ⇒ 钻地让位（这是交错能成立的原因）；
            //   ② **这一格已经被别人占了** ⇒ 同样钻地让位。
            //   ②是把「不重叠」从**概率**变成**结构**的那一步：布局里有几处几何（水塔/管道的
            //   邻格、干道机器的落点）是「查过占用表」才放的，但**盒子本身是按几何摆的**，
            //   两者交汇时总会有几格撞上 —— 实测 5 格（例 (1332,1359) 带#6362 ↔ 机器#10820）。
            //   与其继续推几何不变量（这个项目已经因此返工过三次），不如让**带**这一层
            //   无条件让位：撞上就钻地，谁也别压谁。
            for (int k = 0; k < cells.Length; k++)
            {
                cells[k] = map.To(path[a + k]);
                if (OnTrunk(cells[k]) || !IsFree(cells[k].X, cells[k].Y))
                {
                    mask ??= new bool[cells.Length];
                    mask[k] = true;                                  // 钻地让位
                }
            }
            for (int k = 0; k < cells.Length; k++)
                if (mask == null || !mask[k]) Claim(cells[k].X, cells[k].Y);
            layout.AddBelt(new BeltDef { Cells = cells, TunnelMask = mask });
            prime.Add(rec[i].BeltAfter);
            beltCells += cells.Length;
        }

        // 洗矿机要水：**自己找一格空位**放管道与水塔。
        //
        // ⚠ **不能靠「放机器下方那一格」这种手推的几何不变量**（那格一定空）：
        //   参数一变就破（实测 131 / 1714 格重叠）。这里**试几个候选、查占用表**，
        //   找到就占下、找不到就明确报出来（绝不静默留下一条等不到水的线）。
        // ⚠ 计数必须**只针对真的带洗矿机的产线**：`FindWaterSpot` 对没有洗矿机的产线也返回 false
        //   （它没找到 washer），把它算成「找不到水位」会得出一个自相矛盾的数字
        //   （实测：管道 1,300 条全都建成了，却报「1,300 条找不到水位」）。
        bool hasWasher = false;
        foreach ((string id, _) in rec) if (id == "washer") { hasWasher = true; break; }

        if (hasWasher)
        {
            if (FindWaterSpot(map, path, idx, n, out GridPos pipeCell, out GridPos srcCell))
            {
                AddMachine(layout, content, "fluid-source", srcCell, map.Facing);
                layout.AddPipe(new FluidPipeDef { Cells = new[] { pipeCell } });
                pipeCells += 1;
            }
            else _noWaterCount++;
        }
    }

    /// <summary>
    /// 给洗矿机找一对空位：**管道**贴机器、**水塔**贴管道。
    /// 依次试机器四个正邻方向（局部坐标），第一个「两格都空」的胜出。
    /// </summary>
    private static bool FindWaterSpot(BoxMap map, GridPos[] path, int[] idx, int n,
                                      out GridPos pipeCell, out GridPos srcCell)
    {
        pipeCell = default;
        srcCell = default;
        // 调用方已经确认这条产线**有**洗矿机（见 BuildSerpentine 里的 hasWasher），
        // 所以这里直接取「最后一台机器之前的那台」当作洗矿机位（配方里洗矿机总是倒数第二台）。
        int washerIdx = Math.Max(0, n - 2);
        {
            int i = washerIdx;
            GridPos wl = path[idx[i]];
            // 四个方向：(下方, 右方)、(下方, 左方)、(上方, 右方)、(上方, 左方)
            int[][] tries = { new[] { 0, 1, 1, 1 }, new[] { 0, 1, -1, 1 },
                              new[] { 0, -1, 1, -1 }, new[] { 0, -1, -1, -1 } };
            foreach (int[] t in tries)
            {
                GridPos p = map.To(new GridPos(wl.X, wl.Y + t[1]));
                GridPos s = map.To(new GridPos(wl.X + t[2], wl.Y + t[3]));
                if (p.X < 0 || p.Y < 0 || s.X < 0 || s.Y < 0) continue;
                if (!IsFree(p.X, p.Y) || !IsFree(s.X, s.Y)) continue;
                Claim(p.X, p.Y);        // 管道格在这里占下
                // ⚠ **水塔那一格不在这里占** —— 紧接着的 AddMachine 会占它。
                //   两处都占就会「自己和自己重叠」（占用表实测 1,154 格 = 水塔数，逐位吻合）。
                pipeCell = p;
                srcCell = s;
                return true;
            }
            return false;                       // 这台洗矿机找不到位置（调用方会报出来）
        }
    }

    /// <summary>
    /// 布局自检：把所有被占的格子收进一张表，**重复格数必须为 0**。
    /// 带/管道按路径的每一格算，机器按 <c>SizeX×SizeY</c> 的整个矩形算。
    /// </summary>
    private static void VerifyNoOverlap(FactoryLayout layout, int beltCells)
    {
        var seen = new Dictionary<(int, int), string>(beltCells + 65536);
        int dup = 0;
        string firstDup = "";
        var samples = new List<string>();       // 前几个重叠的**类型组合**（定位用：是带↔机器还是带↔带）

        void Claim(int x, int y, string what)
        {
            if (seen.TryGetValue((x, y), out string? prev))
            {
                dup++;
                if (firstDup.Length == 0) firstDup = $"({x},{y}) {prev} ↔ {what}";
                if (samples.Count < 5) samples.Add($"({x},{y}) {prev} ↔ {what}");
            }
            else seen[(x, y)] = what;
        }

        // ⚠ **地下段不算占位**（这正是交错能成立的原因）：它必须与 FactorySim.Occupy /
        //   快照的跳过口径**完全一致**，否则自检会把合法的交叉报成重叠。
        for (int b = 0; b < layout.BeltCount; b++)
            if (layout.BeltAt(b) is { } belt)
                for (int k = 0; k < belt.Cells.Length; k++)
                    if (!belt.IsTunnelCell(k))
                        Claim(belt.Cells[k].X, belt.Cells[k].Y, $"带#{b}({belt.Cells.Length}格)");

        for (int p = 0; p < layout.PipeCount; p++)
            if (layout.PipeAt(p) is { } pipe)
                foreach (GridPos c in pipe.Cells) Claim(c.X, c.Y, $"管#{p}");

        int machines = 0;
        for (int m = 0; m < layout.MachineSlotCount; m++)
        {
            if (layout.MachineAt(m) is not { } md) continue;
            machines++;
            for (int dx = 0; dx < md.SizeX; dx++)
                for (int dy = 0; dy < md.SizeY; dy++)
                    Claim(md.Pos.X + dx, md.Pos.Y + dy, $"机器#{m}({md.Kind})");
        }

        GD.Print(dup == 0
            ? $"[stress:自检] 无重叠 ✅  占格 = {seen.Count:N0}（带/管/机器 {machines:N0} 台）"
            : $"[stress:自检] ❌ **重叠 {dup:N0} 格**  首例：{firstDup}");
        foreach (string s in samples) GD.Print($"[stress:自检]   例：{s}");
    }
    /// <summary>物品 id（与 ContentDb.DefaultItems 一致；用来决定每条带上装什么）。</summary>
    private const ushort ItemOre = 1, ItemCopperOre = 2, ItemPlate = 3, ItemCopperPlate = 4;
    /// <summary>
    /// 按内容表放一台机器：**必须走 <see cref="BuildableDef.ToMachineDef"/>**，
    /// 这样配方（<c>RecipeIndex</c>）与美术（<c>SpriteCell</c>）都来自内容表 ——
    /// 直接手填 <see cref="MachineDef"/> 会得到一堆没有配方、全用同一张图的机器。
    /// </summary>
    private static void AddMachine(FactoryLayout layout, ContentDb content, string id, GridPos pos, int facing)
    {
        // ⚠ **机器只能让位。** 带可以钻地（掩码），机器不行 —— 所以这里是「不重叠」的最后一道闸：
        //   这一格已经被别人占了就**不放这台机器**，并记一笔（报告里打出来，不静默）。
        //   为什么不能靠调用方各自查：机器有四条投放路径（蛇形产线 / 水塔 / 竖干道 / 横干道），
        //   每条都「查过占用表」也挡不住「后来者」—— 实测 2,850 条线里有 5 台机器落在
        //   已经被带占掉的格子上（例 (1332,1359) 带#6362(279格) ↔ 机器#10820(Producer)）。
        //   把闸门放在**唯一的那条投放函数**里，这类重叠就不可能再出现。
        if (!IsFree(pos.X, pos.Y)) { _machineYielded++; return; }
        BuildableDef b = content.Buildable(id)
            ?? throw new InvalidOperationException($"内容表里没有 '{id}'");
        Claim(pos.X, pos.Y);          // 机器也进占用表（建图期自检才能发现「机器压在带上」）
        // networkId=0：全部挂在同一张电网上（求解是 O(电网数)，一张网最便宜）
        layout.AddMachine(b.ToMachineDef(content, pos, facing, 0, 1));
    }
    /// <summary>
    /// **全厂统一加工时长**（tick/件）。这是本场景「有动态感又不掉量」的关键。
    ///
    /// ⚠ **只把出货机调慢**（如 240 tick/件）确实能稳住数量，
    /// 但代价是**整厂堵死、一件都不动**。
    ///
    /// 正解是让**每一级产能相等**：所有配方与所有无配方机器都用同一个 WorkTicks，
    /// 且各级配方都是 **1 进 1 出**（所以本场景不用 2 进 1 出的齿轮/电路配方 ——
    /// 它们会让某一级消耗速度是上游的 2 倍，流量立刻不守恒）。
    /// 流量守恒 ⇒ 带上的总量**灌进去多少就一直是多少**，同时**一直在流**。
    ///
    /// 剩下的就是别灌满：灌到 <see cref="PrimeFillPercent"/> 留出余量，
    /// 上游才有地方推、整条线才动得起来。
    /// </summary>
    private const int UniformWorkTicks = 60;

    /// <summary>灌带百分比。留余量才有动态；100 = 处处堵死、一件不动。</summary>
    private const int PrimeFillPercent = 70;

    /// <summary>
    /// 造一份内容表副本：**把所有加工时长统一成 <see cref="UniformWorkTicks"/>**，
    /// 其余原样。内容表是「加一种机器 = 加一行数据」的真相来源，所以这里用**派生一份**
    /// 而不是改全局单例 —— 全局那份还要给主场景用。
    /// </summary>
    private static ContentDb MakeStressContent()
    {
        ContentDb d = ContentDb.Default;

        var recipes = new RecipeDef[d.Recipes.Count];
        for (int i = 0; i < recipes.Length; i++)
            recipes[i] = d.Recipes[i] with { WorkTicks = UniformWorkTicks };

        var buildables = new BuildableDef[d.Buildables.Count];
        for (int i = 0; i < buildables.Length; i++)
            buildables[i] = CopyWithWorkTicks(d.Buildables[i], UniformWorkTicks);

        return new ContentDb(d.Items, recipes, buildables);
    }

    private static BuildableDef CopyWithWorkTicks(BuildableDef b, int workTicks) => new()
    {
        Id = b.Id, Name = b.Name, Note = b.Note, Kind = b.Kind, Art = b.Art,
        SizeX = b.SizeX, SizeY = b.SizeY, WorkTicks = workTicks,
        RecipeIndex = b.RecipeIndex, StorageCapacity = b.StorageCapacity,
        FluidRate = b.FluidRate, FluidPerItem = b.FluidPerItem,
        InitialItemType = b.InitialItemType,
    };

    private float CurrentZoom() => _camera?.Zoom.X ?? 1f;

    /// <summary>
    /// 画机器名 label —— 走**通用绘制器**（与主场景同一份代码）。
    /// 用的是 <c>DrawString</c>（CanvasItem 自己的绘制），**不建任何节点**。
    ///
    /// ⚠ z 序见 <see cref="MachineLabelDrawer"/> 的说明：本场景已在 Stress.tscn 里
    ///   把根节点抬到实例层之上（并把子节点设成绝对 z），否则 label 会被带面盖住。
    /// ⚠ 必须传 <c>GetViewportTransform()</c>（含相机与画布拉伸），label 靠它抵消画布缩放；
    ///   传 <c>CurrentZoom()</c>（只有相机那一半）的话，全屏拉伸时字号会差一截。
    /// </summary>
    public override void _Draw() => _labels?.Draw(this, _viewRect, GetViewportTransform());

    public override void _Process(double delta)
    {
        _elapsed += delta;

        if (_autoExitSet && _elapsed >= _autoExitSec)
        {
            Report("final");
            _runner?.Stop();
            GetTree().Quit();
            return;
        }

        if (_screenshotPath != null && !_shotTaken && _elapsed >= _screenshotAtSec)
        {
            _shotTaken = true;
            SaveScreenshotAsync(_screenshotPath);
        }

        if (_runner == null || _sim == null || _bridge == null) return;

        // ① 推视口（必须在认领快照之前 —— 剔除发生在 Sim 线程的填充里）。
        PublishViewport();

        // 自动横扫：用来验证「剔除率随视口移动而变」且画面**不闪空**
        //（段级包围盒少一圈余量的话，边缘会随相机移动一闪一闪）。
        // 沿**世界中心纵轴**来回扫，而不是写死一条线 —— 世界多大它就有多长。
        if (_autoPan && _camera != null)
        {
            float cx = (_worldBounds.LoX + _worldBounds.HiX) * 0.5f;
            float span = _worldBounds.HiY - _worldBounds.LoY;
            float t = (float)Math.Sin(_elapsed * 0.35);          // -1..1
            _camera.Position = new Vector2(cx, (_worldBounds.LoY + _worldBounds.HiY) * 0.5f + t * span * 0.45f);
        }

        if (!_sim.Buffers.TryClaim(out RenderSnapshot snapshot)) return;

        // 异常诊断：可见实例如突然暴涨，说明**剔除矩形**被改大了（相机被拉远）。
        // ⚠ LOD 生效时（拉远）「实例数变多」是**正常的**（一格代表一片），不算异常 ——
        //   否则会刷出上千条假警告（实测 1462 条）。
        if (_sim.CullEnabled && _sim.LodStride == 1 && snapshot.LiveCount > AnomalyThreshold)
        {
            _anomalyCount++;
            if (_anomalyCount <= 3)
            {
                _sim.CullViewport.TryGet(out WorldRect bad);
                GD.Print($"[stress:异常] 快照写出 {snapshot.LiveCount:N0} 个实例（应有约 8.6k）—— " +
                         $"此刻剔除矩形=({bad.LoX:F0},{bad.LoY:F0})..({bad.HiX:F0},{bad.HiY:F0})  " +
                         $"相机={_camera?.Position} zoom={CurrentZoom():F3}  " +
                         $"视口={GetViewportRect().Size}  ");
            }
        }

        _submitClock.Restart();
        _bridge.Submit(snapshot);
        _submitClock.Stop();
        _sim.Buffers.Release();

        double submitMs = _submitClock.Elapsed.TotalMilliseconds;
        _submitSumMs += submitMs;
        if (submitMs > _submitMaxMs) _submitMaxMs = submitMs;
        _fillSumMs += _bridge.LastFillMs;
        if (_bridge.LastFillMs > _fillMaxMs) _fillMaxMs = _bridge.LastFillMs;
        _setBufSumMs += _bridge.LastSetBufferMs;
        if (_bridge.LastSetBufferMs > _setBufMaxMs) _setBufMaxMs = _bridge.LastSetBufferMs;
        // 快照填充发生在 Sim 线程，读的是它上一次的耗时（这就是渲染侧预算的主体）
        _snapSumMs += _sim.LastSnapshotMs;
        if (_sim.LastSnapshotMs > _snapMaxMs) _snapMaxMs = _sim.LastSnapshotMs;
        _framesSinceReport++;

        UpdateHud(snapshot);

        if (_probe) ReportPerSecond();
    }

    /// <summary>
    /// 把当前可见的世界矩形推给模拟（剔除用）。
    /// 取视口四角再过 `AffineInverse` —— 相机平移/缩放/画布拉伸全都自动算进去，
    /// 且与 Godot 自己的绘制变换**同源**（自己拿「相机位置 + 视口一半」手算过一次，
    /// 结果是注入坐标整体偏掉）。
    /// </summary>
    private void PublishViewport()
    {
        Transform2D inv = GetViewportTransform().AffineInverse();

        // ⚠⚠ **屏幕范围必须用「物理窗口像素」，不能用 `GetViewportRect().Size`。**
        //
        //   这两者**不是同一个坐标空间**，而 `GetViewportTransform()` 输出的是**窗口像素**：
        //     · `GetViewportRect().Size` = **逻辑视口**尺寸（本项目 1920×1080，
        //       全屏按 `aspect=expand` 会变成 1920×1200 之类）；
        //     · `GetViewportTransform()` 的缩放 = `camera.zoom × stretch`，
        //       而 `stretch = 窗口 / 逻辑视口`。
        //
        //   ⇒ 拿逻辑视口当屏幕范围去反解，会**同时算错偏移和尺寸**。
        //     实测全屏（窗口 2560×1600、逻辑 1920×1200、stretch 1.3333、zoom 0.5）：
        //       变换 scale = 0.6667，O = (-4362.667, -41866.668)
        //       错误矩形 = (6544,62800)..(9424,64600)  ← 中心 (7984,63700)，偏左上且只有 75% 大
        //       正确矩形 = (6544,62800)..(10384,65200) ← 中心 (8464,64000) = 相机位置 ✅
        //     **窗口模式下两者恰好相等 ⇒ 这个 bug 只在全屏暴露**。
        Vector2 size = GetWindow().Size;

        Vector2 p0 = inv * Vector2.Zero;
        Vector2 p1 = inv * new Vector2(size.X, 0f);
        Vector2 p2 = inv * new Vector2(0f, size.Y);
        Vector2 p3 = inv * size;

        float loX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float hiX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float loY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float hiY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

        _sim!.CullViewport.Set(loX, loY, hiX, hiY,
            GetViewportTransform().Scale.X);   // ← 世界→屏幕缩放（含 stretch）：Sim 侧靠它判亚像素实例

        // label 裁剪用（渲染线程自己的副本，见 _machineLabels 的说明）
        _viewRect = new Rect2(loX, loY, hiX - loX, hiY - loY);

        // 机器名 label：**只要相机动过就要重画**。
        // ⚠ 不能写成「只在 zoom ≥ MinZoom 时才重画」—— 拉远到阈值以下时如果不再重画，
        //   画布上最后一次画的 label 会**留在屏幕上**（`_Draw` 不再被调用就没人清）。
        //   `ShouldRedraw` 把「上一次画过东西」也算进去，于是跨过阈值时会多画一次把旧的清掉。
        if (_labels is { } ld && ld.ShouldRedraw(CurrentZoom())) QueueRedraw();

        // 诊断：头几帧把「视口像素 → 世界矩形」原样打出来（FORGEFLOW_VPDIAG=1）。
        // 上传容量会被「可见实例变多」触发扩容，而可见实例数完全由这个矩形决定 ——
        // 所以这里出问题时，唯一的抓手就是把这个矩形打出来看（不要靠猜）。
        if (_vpDiagFrames > 0 && _vpDiagOn)
        {
            _vpDiagFrames--;
            bool bad = !_sim.CullViewport.TryGet(out WorldRect r);
            GD.Print($"[vpdiag] 视口={size} 变换={GetViewportTransform()}  " +
                     $"世界矩形={(bad ? "无" : $"({r.LoX:F0},{r.LoY:F0})..({r.HiX:F0},{r.HiY:F0})")}  " +
                     $"相机={_camera?.Position} zoom={CurrentZoom():F3}  " +
                     $"drawn={_sim.LiveCount:N0}/cap={_sim.Buffers.Capacity:N0}  " +
                     $"上传容量={_bridge?.UploadCapacity:N0}");
        }
    }

    private int _vpDiagFrames = 8;
    private readonly bool _vpDiagOn =
        Environment.GetEnvironmentVariable("FORGEFLOW_VPDIAG") == "1";

    /// <summary>可见实例超过这个数就打一行异常诊断（剔除矩形算歪了才会这么大）。</summary>
    private const int AnomalyThreshold = 20000;

    private int _anomalyCount;

    /// <summary>BuildSim 里算好的取景，用来判断「这轮跑测期间相机有没有被移动过」。</summary>
    private Vector2 _framedPos;
    private float _framedZoom;
    private bool _framed;

    private void UpdateHud(RenderSnapshot snapshot)
    {
        FactorySim f = _sim!;
        long ticks = _runner?.StepCount ?? 0;
        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (nowSec - _lastTickSec >= 1.0)
        {
            double rate = (ticks - _lastTicks) / (nowSec - _lastTickSec);
            _lastTicks = ticks;
            _lastTickSec = nowSec;
            _hudStatus!.Text = $"逻辑 {rate:F1} tick/s（目标 {SimLoopRunner.TickRate:F0}）    " +
                               $"渲染 {Engine.GetFramesPerSecond():F0} FPS    tick {ticks:N0}";
        }

        int drawn = snapshot.LiveCount;
        int culled = f.LastCulledInstances;
        int total = drawn + culled;
        double rate2 = total > 0 ? 100.0 * culled / total : 0;

        _hudInstances!.Text = f.CullEnabled
            ? $"写出实例 {drawn:N0}    剔除 {culled:N0}（{rate2:F2}%）    实例总数 {total:N0}"
            : $"写出实例 {drawn:N0}    剔除 **关**（FORGEFLOW_NO_CULL=1）    实例总数 {total:N0}";

        _hudTiming!.Text = $"**快照填充(Sim线程) {f.LastSnapshotMs:F2}ms**    " +
                           $"桥接 staging {_bridge!.LastFillMs:F3}ms    " +
                           $"SetBuffer {_bridge.LastSetBufferMs:F3}ms    interop {_bridge.InteropCallsLastFrame}/帧";

        _hudWorld!.Text = $"带上物品 {f.ItemsOnBelts:N0}    出货 {f.ShippedTotal:N0} 件    " +
                          $"机器 {f.MachineCount:N0}    带 {f.BeltCount:N0}";
    }

    private void ReportPerSecond()
    {
        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (nowSec - _lastReportSec < 1.0) return;
        _lastReportSec = nowSec;
        Report("1s");
    }

    private void Report(string tag)
    {
        if (_sim == null || _bridge == null) return;
        int n = Math.Max(_framesSinceReport, 1);
        FactorySim f = _sim;
        int drawn = f.LiveCount;
        int culled = f.LastCulledInstances;
        int total = drawn + culled;
        double rate = total > 0 ? 100.0 * culled / total : 0;
        int stride = _bridge.Stride;
        long ticks = _runner?.StepCount ?? 0;
        double nowSec = Time.GetTicksMsec() / 1000.0;

        // ⚠ 逻辑帧率必须是**区间速率**，不能是「累计 tick / 累计秒」。
        //   后者会把启动那几秒的慢速永远摊在里面（实测它会从 1.5 一路爬到 40 才收敛），
        //   看上去像「越跑越快」，其实只是平均值在追上来 —— 一个会骗人的指标。
        //   （本项目纪律：指标异常先怀疑指标本身。）
        double dt = nowSec - _repLastSec;
        double tickRate = dt > 0 ? (ticks - _repLastTicks) / dt : 0;
        _repLastTicks = ticks;
        _repLastSec = nowSec;

        // 「在动」必须是个**可读的数**，不能靠盯着画面猜：
        //   · 带上物品数**恒定**只说明「总量守恒」—— 整厂堵死时它同样恒定；
        //   · 真正证明「在流」的是**出货在涨**（物品被消耗 ⇒ 带在走 ⇒ 上游在生产）。
        //     堵死时出货会停在 0 附近不动。这两个数一起看才说明「稳在百万件**且**在流」。
        long shipped = f.ShippedTotal;
        double shipRate = dt > 0 ? (shipped - _repLastShipped) / dt : 0;
        _repLastShipped = shipped;
        double fill = _totalBeltCells > 0 ? 100.0 * f.ItemsOnBelts / _totalBeltCells : 0;

        GD.Print($"[stress:{tag}] 逻辑 {tickRate:F1} tick/s  渲染 {Engine.GetFramesPerSecond():F0} FPS  " +
                 $"带上物品={f.ItemsOnBelts:N0}（占带格 {fill:F1}%）  出货 {shipped:N0}（{shipRate:F0}/秒）");
        GD.Print($"[stress:{tag}] 写出实例={drawn:N0}  剔除={culled:N0}（{rate:F2}%）  总数={total:N0}  " +
                 $"剔除开关={(f.CullEnabled ? "开" : "关")}");
        GD.Print($"[stress:{tag}] **快照填充 avg={_snapSumMs / n:F2} max={_snapMaxMs:F2}ms（Sim 线程，§6.1 的渲染侧预算）**   " +
                 $"桥接 staging avg={_fillSumMs / n:F3} max={_fillMaxMs:F3}ms   " +
                 $"SetBuffer avg={_setBufSumMs / n:F3} max={_setBufMaxMs:F3}ms");
        GD.Print($"[stress:{tag}] 实际上传 = {_bridge.UploadCapacity:N0} 实例 = " +
                 $"{_bridge.LastUploadMb:F2} MB/帧（画出 {drawn:N0}）；" +
                 $"快照容量 {f.Buffers.Capacity:N0} 实例 = " +
                 $"{f.Buffers.Capacity * (long)stride * 4 / 1024.0 / 1024.0:F1} MB/帧");

        // ⚠ **测量卫生**：剔除率与帧时间都随视口变化，而相机是**可以被鼠标滚轮移动**的
        //   （实测物理鼠标在窗口上滚一下就够：16 次滚轮 ⇒ 缩放 0.5→0.1、可见实例 8.6k→5万、
        //    154 帧异常暴涨、上传容量被顶到 618,496）。这本身不是 bug，但它会让**跑测数字失效**。
        //   所以这里显式报出来 —— 宁可说你这次测的不算，也不要给一个被污染的漂亮数字。
        if (_anomalyCount > 0)
            GD.Print($"[stress:{tag}] ⚠ 有 {_anomalyCount} 帧可见实例异常暴涨（剔除矩形被改大过）");
        if (_framed && _camera != null &&
            (MathF.Abs(_camera.Zoom.X - _framedZoom) > 1e-3f ||
             _camera.Position.DistanceTo(_framedPos) > 1f))
            GD.Print($"[stress:{tag}] ⚠⚠ **相机在本次测量中被移动过**" +
                     $"（取景 {_framedZoom:F3}@{_framedPos} → {_camera.Zoom.X:F3}@{_camera.Position}）" +
                     $"⇒ 剔除率与帧时间都随视口变化，这组数字**不可与其它运行直接比较**。" +
                     $"要可复现的测量请设 FORGEFLOW_NO_CAMERA=1（取景仍生效、输入被忽略）");

        // 渲染诊断（FORGEFLOW_RENDERDIAG=1）：**把"MultiMesh 有没有被藏起来"变成可读的数**。
        //
        // 为什么需要它：亚像素取景下画面全是空的，而所有计数（写出 33,935、interop=1、无报错）
        // 看起来都正常 —— 「跑通」和「画出来」是两件事。这一行把两边都摆出来：
        // 左边是引擎侧的绘制状态（节点/材质/纹理/实例数/CustomAabb），右边是相机与视口。
        // 判据：节点可见 + 材质纹理在 + VisibleInstanceCount>0 + CustomAabb 盖住相机可见范围
        //       ⇒ MultiMesh **没有被裁掉**，画出来看不见纯粹是因为一个实例不到一个像素。
        if (Environment.GetEnvironmentVariable("FORGEFLOW_RENDERDIAG") == "1" && _items != null && _multiMesh != null)
        {
            Vector2 vp = GetViewportRect().Size;
            float z = MathF.Max(CurrentZoom(), 1e-4f);
            Vector2 cam = _camera?.Position ?? Vector2.Zero;
            var live = new WorldRect(cam.X - vp.X * 0.5f / z, cam.Y - vp.Y * 0.5f / z,
                                     cam.X + vp.X * 0.5f / z, cam.Y + vp.Y * 0.5f / z);
            Aabb aabb = _multiMesh.CustomAabb;
            bool covers = aabb.Position.X <= live.LoX && aabb.Position.Y <= live.LoY &&
                          aabb.Position.X + aabb.Size.X >= live.HiX &&
                          aabb.Position.Y + aabb.Size.Y >= live.HiY;
            GD.Print($"[renderdiag] 节点 Visible={_items.Visible}  材质={_items.Material != null}  " +
                     $"纹理={_items.Texture != null}  InstanceCount={_multiMesh.InstanceCount:N0}  " +
                     $"VisibleInstanceCount={_multiMesh.VisibleInstanceCount:N0}  " +
                     $"CustomAabb=({aabb.Position.X:F0},{aabb.Position.Y:F0}) 尺寸({aabb.Size.X:F0}×{aabb.Size.Y:F0})  " +
                     $"盖住可见范围={covers}");
            GD.Print($"[renderdiag] 相机={cam} zoom={z:F4} 视口={vp} 可见世界=" +
                     $"({live.LoX:F0},{live.LoY:F0})..({live.HiX:F0},{live.HiY:F0})  " +
                     $"一格={z * FactorySim.CellSizePx:F2} 屏幕像素  LOD步长={f.LodStride}");
        }

        _framesSinceReport = 0;
        _fillSumMs = _setBufSumMs = _submitSumMs = _snapSumMs = 0;
        _fillMaxMs = _setBufMaxMs = _submitMaxMs = _snapMaxMs = 0;
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;

    /// <summary>
    /// 截图：等 FramePostDraw（否则拿到空图或上一帧 —— 与主场景同一套做法）。
    /// 用 <c>_console.exe</c> 跑才能看到 GD.Print。
    /// </summary>
    private async void SaveScreenshotAsync(string path)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image? img = GetViewport()?.GetTexture()?.GetImage();
        if (img == null)
        {
            GD.PushError("[stress] 截图失败：拿不到视口图像");
            return;
        }
        Error err = img.SavePng(path);
        GD.Print(err == Error.Ok ? $"[stress] 截图已保存 {path}" : $"[stress] 截图失败 err={err}");
    }
}
