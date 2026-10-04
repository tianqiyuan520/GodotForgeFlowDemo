using System;
using System.Diagnostics;
using ForgeFlow.Sim;
using Godot;

// Godot C# 坑：Godot 自己有一个 Environment 类（3D 环境资源），
// 与 System.Environment 同名。同时 using Godot 和 using System 会得到 CS0104 歧义。
using Environment = System.Environment;

// =============================================================================
// WalkingSkeleton —— 集成骨架 + 玩家交互层。
//
// ⚠ 工程约定：**节点与资源一律在场景（Scenes/Main.tscn）里预先建好**，
//   本脚本不 new Node、不 AddChild。脚本只负责行为与绑定。
//   需要的节点通过 [Export] 声明或在场景里按固定名解析。
//
// 它验证三件**不能靠推理确定**的事：
//   ① SimLoop 独立线程与 Godot 主/渲染线程能否稳定共存（长跑）
//   ② MultiMesh 的 2D 实例布局（8 float 变换 + 4 float 颜色）是否正确
//   ③ 每帧「构造实例缓冲 + 提交」的真实成本与 interop 次数
//
// ── 交互层（建造 → 撤销 → 蓝图）────────────────────────────────────────────
//   玩家的「当前操作」由一个**显式状态机**管（见 BuildState）：同一时刻只可能处在一个状态里，
//   鼠标按下/松开该做什么只由那张 switch 决定，不存在「拖框选时工具却是拆除」这类组合。
//   可建造项用「内容表下标」表示，**不是写死的机器种类** —— 内容表加一行，工具栏与快捷键自动多一项。
//   1-9 / 0 / - / =      选可建造项（选新的机器会把朝向归零）
//   B 铺带(拖拽，实时显示预计位置)  E 拆除(高亮目标)  V 框选复制  R 旋转朝向  Esc 取消
//   Ctrl+Z 撤销   Ctrl+Y 重做   Ctrl+C 复制选中区   Ctrl+V 蓝图粘贴(左键落，可连贴)
//   相机控制见 Scripts/CameraController.cs（场景里挂在 Camera 节点上）。
//
// 环境变量：
//    FORGEFLOW_AUTOEXIT=<秒>     到点自动退出（自动化跑测用）
//    FORGEFLOW_PROBE=1           每秒打印统计
//    FORGEFLOW_SCREENSHOT=<路径> 运行中抓一张视口截图（绝对路径，PNG）
//    FORGEFLOW_SCREENSHOT_AT=<秒>
//    FORGEFLOW_HUD=0             隐藏 HUD
//    FORGEFLOW_TOOL=<id|belt|erase|select|none>  启动时的默认状态（内容表 id，如 iron-mine）
//    FORGEFLOW_SIMCLICK="tool,x,y"   通过**真实输入管线**注入一次点击（自动化验证）
//    FORGEFLOW_SIMSEQ="..."      脚本化输入序列（见 ExecuteSeqStep 的说明；
//                                除格坐标动作外还有轨道选择器的 pick:<行> / bind:in|out|clear）
//    FORGEFLOW_MOUSEDIAG=1       打印「事件坐标 vs 真实光标」的差值
//    FORGEFLOW_AUTOBUILD=1       脚本化搭一条完整产线（含机械臂跨格与流体）
//    FORGEFLOW_NO_CAMERA=1       停用相机控制（截图要可复现时用）
//    FORGEFLOW_SIM / BELTS / BELT_CELLS / ...  见 CreateSim
// =============================================================================

public partial class WalkingSkeleton : Node2D
{
    /// <summary>
    /// 物品层。**节点在 Scenes/Main.tscn 里预先建好**，本脚本不创建节点。
    /// 解析顺序：Inspector 绑定（若在编辑器里拖过）→ 按固定名 "ItemMultiMesh" 查找。
    /// 两者都失败就报错退出，而不是动态建节点 —— 工程约定优先于「能跑就行」。
    /// </summary>
    [Export] public MultiMeshInstance2D ItemMultiMesh = null!;

    [Export] public int InstanceCount = 100_000;

    /// <summary>
    /// 实例的基础边长（px）。**= 图集瓦片原生尺寸 = 32**，
    /// 这样带面/机器/物品三者同比例（sierrassets 美术是 32×32 瓦片）。
    /// 必须同时传给 FactorySim 的 unitSizePx。
    /// </summary>
    [Export] public float ItemSizePx = 32f;
    [Export] public float ItemSpeed = 120f;

    /// <summary>相机 zoom=1 时可见的世界范围（1920×1080）。布局要在这个范围内摆得下。</summary>
    private const float VisibleWorldWidth = 1920f;
    private const float VisibleWorldHeight = 1080f;

    private SimLoopRunner? _runner;
    private RenderBridge _bridge = null!;
    private ISimSource? _sim;

    private bool _probe;
    private double _autoExitSec = -1;
    private string? _screenshotPath;
    private double _screenshotAtSec = 5;
    private bool _shotTaken;
    private bool _diagXform;
    private int _lastSimTick;
    private int _lastLiveCount;
    private float _lastBeltScroll;

    // ── HUD ─────────────────────────────────────────────────────────────────
    // 节点全部在场景里预建（遵守工程约定），这里只做解析 + 改 text。
    private CanvasLayer? _hud;
    private Label? _hudStatus, _hudThroughput, _hudMachines, _hudInstances, _hudPacing, _hudHint, _hudBuild;
    private Label? _toolbarTitle;
    private Sprite2D? _ghost;
    private ColorRect? _selection;

    /// <summary>相机（只读 <see cref="CameraController.SubPixel"/>：亚像素取景时 HUD 要说明原因）。</summary>
    private CameraController? _camera;

    // ── 预览矩形池（铺带的预计位置 / 拆除目标高亮）────────────────────────────
    //
    // 为什么是「几个矩形」而不是「逐格描边」：
    //   ① 一段带永远是横或竖的**直线**（模拟侧 DrawBelt 只接受横/竖），机器占地也是矩形
    //      ⇒ 占地天然就是一个矩形，逐格画反而更啰嗦；
    //   ② 节点必须**在场景里预建**（工程约定），所以固定开出 3 个矩形复用（L 形拖拽 = 2 段）。
    private ColorRect?[] _previewRects = Array.Empty<ColorRect>();
    private int _previewUsed;

    private static readonly Color PreviewOkColor = new(0.45f, 0.72f, 1f, 0.30f);   // 淡蓝：可以铺（低轨）
    private static readonly Color PreviewBadColor = new(1f, 0.42f, 0.38f, 0.34f);  // 淡红：有冲突
    private static readonly Color HoverColor = new(0.55f, 0.85f, 1f, 0.20f);       // 淡青：鼠标下可以选/可以拆

    /// <summary>
    /// **高轨**的"可铺"色（淡紫）。
    ///
    /// 为什么要给层配颜色：层是**看不见的**建造参数，铺之前玩家没有任何视觉依据判断
    /// "我这一下是铺在上面还是下面"。颜色是唯一能在松手前把这个参数显示出来的东西，
    /// 而且它复用了已有的预览层 tint uniform（一分钱额外成本都没有）。
    /// </summary>
    private static readonly Color PreviewOkHighColor = new(0.78f, 0.6f, 1f, 0.34f);

    /// <summary>按**实际会铺到的那一层**取预览色（自动交错时颜色要跟着变，否则预览在骗人）。</summary>
    private static Color BeltOkColor(int level) => level == 0 ? PreviewOkColor : PreviewOkHighColor;

    /// <summary>
    /// **选中态的染色**（乘在图集上，见预览层的 tint）。
    ///
    /// ⚠ 大于 1 的通道是刻意的：乘法**能变亮**（美术本身是深蓝/深灰，×1.5 明显亮一档），
    ///   所以「被选中」= 物块自己发亮，而不是在它上面盖一个半透明框 ——
    ///   后者会在选中一整片时糊成一大块，看不出到底选中了哪些东西。
    /// ⚠ 倍数要够狠才看得出来：按 (1.15, 1.5, 1.85) 这类小倍数，深蓝机器只亮了 4% —— 肉眼看不出；
    ///   实测（与原图逐像素比）机器平均差 21.9、带 44.0 时，带很醒目但机器仍偏暗，
    ///   所以这里的倍数必须更大 + 偏青，让两类物块都一眼看出「被选中」。
    /// </summary>
    private static readonly Color SelectionTint = new(1.6f, 2.05f, 2.5f);

    // ── 建造交互状态机 ──────────────────────────────────────────────────────
    /// <summary>
    /// **用户操作状态机。**
    ///
    /// 为什么要有它：`_tool` + `_dragging` + `_selecting` + `_pasting` 四个字段
    /// **组合**出来的「玩家现在在做什么」存在一堆能表达出来却毫无意义的组合
    /// （例如「正在拖框选，工具却是拆除」）。这些非法组合只能靠各处 if 的约定去避免，
    /// 每加一种工具都要把全部组合重推一遍。
    /// 状态机把「同一时刻只能处于一个操作中」变成**不可表示**：鼠标按下/松开该做什么，
    /// 只由当前状态的唯一一张 switch 决定；切换状态时统一做进入动作
    /// （复位朝向、丢弃拖拽锚点、退出粘贴模式）。
    ///
    /// ⚠ 注意「选中的东西」**不是状态而是数据**（<c>_selCell</c> / <c>_selA.._selB</c>）：
    ///   它要在状态之间来回切换时保留，所以不放进状态机。
    ///
    /// 鼠标分工（玩家只需要记四条）：
    ///   · **左键**：放置（选了建筑）/ 铺带拖拽（选了铺带）/ **单击 = 选中**（空手，顺带显示信息）
    ///   · **右键单击 = 拆掉鼠标下那一个** —— 所以**不需要「拆除模式」**
    ///   · **右键拖拽 = 框选**（松开**只选中**；按 `X` / `Delete` 才拆）⇒ 见 <see cref="BuildState.BoxSelect"/>
    ///   · `Ctrl+C` 复制框选 · `Ctrl+Z/Y` 撤销重做 · `Esc` 回空手
    /// </summary>
    private enum BuildState
    {
        Idle,         // 空手：左键单击选中（显示信息），右键单击拆一个，右键拖拽框选
        Placing,      // 选好了一种建筑，左键落一台；R 旋转
        BeltIdle,     // 铺带工具已选、还没按下
        BeltDrag,     // 铺带按住中（锚点有效，画预计位置）
        BoxSelect,    // 右键按住拖拽中（松开回到 _returnState）
        Paste,        // 蓝图粘贴（左键落点，可连贴）
    }

    private BuildState _state = BuildState.Idle;

    /// <summary>框选是从哪个状态进来的 —— 松开要回到它（框选只是「借一下右键」，不改左键模式）。</summary>
    private BuildState _returnState = BuildState.Idle;

    private static bool IsDragging(BuildState s) => s is BuildState.BeltDrag or BuildState.BoxSelect;

    /// <summary>
    /// 状态切换 —— **所有进入动作集中在这里**。
    /// 不写「顺便也把那个字段改一下」：那正是四个布尔字段互相纠缠的成因。
    /// </summary>
    private void SetState(BuildState next)
    {
        if (IsDragging(_state)) _dragAnchor = default;   // 离开拖拽状态就丢掉锚点
        _state = next;

        // ⚠ 一进入「放机器」就把朝向归零：上一次那台机器转过的角度对新选的东西没有意义，
        //   否则蓝图会带着一个玩家没主动设过的旋转冒出来。
        if (next == BuildState.Placing) _facing = 0;

        RefreshToolbarHighlight();
    }

    /// <summary>当前选中的可建造项在 <see cref="ContentDb.Buildables"/> 里的下标。</summary>
    private int _buildableIndex;

    /// <summary>朝向 0..3（东/南/西/北），纯外观。</summary>
    private int _facing;

    /// <summary>
    /// **当前要铺在哪一层**（0 = 低轨、1 = 高轨），由 `T` 切换。
    ///
    /// 为什么是"当前层"这种建造参数、而不是"每条带插一个下拉框"：
    ///   层的唯一作用是**让两条带能十字交错**，玩家铺带时心里想的就是"这条走上面还是下面"。
    ///   做成模态参数与朝向（`R`）完全同构 —— 顺带继承相同的状态机纪律（进 Placing 复位）。
    /// </summary>
    private int _beltLevel;

    // ── 轨道选择器（点机器 → 选它绑哪条轨道）────────────────────────────────
    // 节点在 Scenes/Main.tscn 里预建（工程约定），这里只解析 + 连线 + 改文本。
    private PanelContainer? _trackPicker;
    private ItemList? _trackList;
    private Label? _trackTitle;
    private Button? _btnBindIn, _btnBindOut, _btnClear, _btnClose;

    /// <summary>选择器当前服务的机器格（null = 没在给任何机器选轨道）。</summary>
    private GridPos? _pickerMachineCell;

    /// <summary>候选轨道（本帧重算）—— 列表条目与按钮作用的都是它里面的 id。</summary>
    private readonly List<int> _pickerBelts = new();

    /// <summary>选择器里选中的那条轨道（世界里会**提亮它整段**，复用选中态的高亮）。</summary>
    private int _pickerBelt = -1;

    /// <summary>候选表是按哪台机器 / 哪个拓扑版本算出来的（用来避免每帧重扫全部带格）。</summary>
    private int _pickerMachine = -1;
    private int _pickerRevision = -1;
    private double _pickerRowsAt;

    /// <summary>
    /// **单击选中的那一格**（左键单击 = 选中，HUD 显示那个机器 / 带 / 管道的信息）。
    /// 存**格子**而不是 id：id 会随拆除失效，而格子正是玩家看到的东西。
    /// null = 没选中任何东西。
    /// </summary>
    private GridPos? _selCell;

    /// <summary>
    /// 框选出来的矩形（右键拖拽）。与 <see cref="_selCell"/> **互斥**：最后选的那个说了算。
    /// `Ctrl+C` 复制、`X`/`Delete` 拆除都用它。
    /// </summary>
    private GridPos _selA, _selB;
    private bool _hasSelection;
    private GridPos _dragAnchor;
    private GridPos _hover;
    private bool _sawMotionThisFrame;
    private bool _autoBuilt;
    private bool _syntheticClickDone;
    private bool _clickResultChecked;
    private int _preClickMachines;
    private double _hudLastUpdateSec;
    private const double HudIntervalSec = 0.2;   // 5 Hz：人眼够了，也避免每帧改文本

    /// <summary>内容表 + 工具栏按钮（场景预建，按固定名绑定）。</summary>
    private ContentDb _content = null!;
    private Button[] _toolButtons = Array.Empty<Button>();

    /// <summary>蓝图预览层：复用场景里的 ProbeMultiMesh（诊断时它另有用途，两者不会同时用）。</summary>
    private RenderBridge? _previewBridge;
    private RenderSnapshot? _previewSnapshot;

    /// <summary>工具栏里那个「铺带」按钮（它不是可建造项，所以单独接线、单独高亮）。</summary>
    private Button? _beltButton;

    /// <summary>工具栏里每个可建造项的「名称 / 作用」标签（与按钮一一对应，来自内容表）。</summary>
    private Label?[] _toolLabels = Array.Empty<Label?>();

    /// <summary>选中信息行（左键单击选中的机器 / 带 / 管道）。</summary>
    private Label? _hudInfo;

    /// <summary>左上角的**物品图例**（内容由 ContentDb 填，启动时一次）。</summary>
    private Label? _hudItems;

    // 出货速率用滑动窗口：记「上次采样」而不是「上次更新」，窗口太短会抖
    private long _hudRateLastShipped;
    private double _hudRateLastSec;
    private double _hudRatePerMin;
    private long _hudTickLastCount;
    private double _hudTickLastSec;
    private double _hudTickRate;
    private double _elapsed;
    private int _framesSinceReport;
    private double _submitAccumMs;
    private double _submitMaxMs;
    private double _fillSumMs;
    private double _fillMaxMs;
    private double _setBufSumMs;
    private double _setBufMaxMs;
    private long _lastReportedTick;
    private double _lastReportTickTime;

    private readonly Stopwatch _submitClock = new();

    public override void _Ready()
    {
        MultiMeshInstance2D? target = ItemMultiMesh;
        if (target != null && GodotObject.IsInstanceValid(target))
        {
            GD.Print("[skeleton] ItemMultiMesh 来自 Inspector 绑定");
        }
        else
        {
            target = GetNodeOrNull<MultiMeshInstance2D>("ItemMultiMesh");
            if (target != null) GD.Print("[skeleton] ItemMultiMesh 按固定名 'ItemMultiMesh' 解析");
        }

        if (target == null || target.Multimesh == null)
        {
            GD.PushError("[skeleton] 找不到 ItemMultiMesh 或其 MultiMesh 资源为 null。" +
                         "节点与资源必须在 Scenes/Main.tscn 里预先创建 —— 本脚本不会动态建节点。");
            return;
        }

        ItemMultiMesh = target;
        MultiMesh multiMesh = target.Multimesh;

        // 相机（只读它的 SubPixel 标志）：亚像素取景时 HUD 要明确说「看不到轨道是预期的」，
        // 否则玩家看到的是一屏网格 + 一堆正常计数，只会以为渲染坏了。
        _camera = GetNodeOrNull<CameraController>("Camera");

        // 诊断：把对照 Sprite2D 显示出来，用来区分「截图管线坏了」还是「MultiMesh 没画」。
        Sprite2D? diag = GetNodeOrNull<Sprite2D>("DiagSprite");
        if (diag != null)
            diag.Visible = Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_SPRITE") == "1";

        // 诊断：最小配置的 MultiMesh 探针。写 1 个实例、放大 200 倍、放在屏幕正中。
        MultiMeshInstance2D? probe = GetNodeOrNull<MultiMeshInstance2D>("ProbeMultiMesh");
        if (probe?.Multimesh != null && Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_MULTIMESH") == "1")
        {
            MultiMesh pm = probe.Multimesh;
            pm.InstanceCount = 1;
            pm.SetInstanceTransform2D(0, new Transform2D(0f, new Vector2(200f, 200f), 0f, new Vector2(960f, 540f)));
            probe.Visible = true;
            GD.Print($"[diag] ProbeMultiMesh: count={pm.InstanceCount} mesh={(pm.Mesh != null)} " +
                     $"tex={(probe.Texture != null)} mat={(probe.Material != null)} visible={probe.Visible}");
        }

        bool layoutProbe = Environment.GetEnvironmentVariable("FORGEFLOW_LAYOUT_PROBE") == "1";
        _probe = Environment.GetEnvironmentVariable("FORGEFLOW_PROBE") == "1";
        _cmdDiag = Environment.GetEnvironmentVariable("FORGEFLOW_CMDDIAG") == "1";

        string? autoExit = Environment.GetEnvironmentVariable("FORGEFLOW_AUTOEXIT");
        if (!string.IsNullOrEmpty(autoExit) && double.TryParse(autoExit, out double sec))
            _autoExitSec = sec;

        _screenshotPath = Environment.GetEnvironmentVariable("FORGEFLOW_SCREENSHOT");
        string? shotAt = Environment.GetEnvironmentVariable("FORGEFLOW_SCREENSHOT_AT");
        if (!string.IsNullOrEmpty(shotAt) && double.TryParse(shotAt, out double sa) && sa > 0)
            _screenshotAtSec = sa;

        ApplyEnvOverrides();
        _content = ContentDb.Default;

        if (layoutProbe)
        {
            RunLayoutProbe(multiMesh);
            return;
        }

        // 先建模拟：InstanceCount 必须等于快照容量，否则 SetBuffer 会被引擎断言拒绝。
        _sim = CreateSim();
        int capacity = _sim.Buffers.Capacity;

        // ⚠ CustomAabb 必须**盖住整个工厂**，不能写死成 1920×1080。
        //   写死的话，百万规模那个铺满上万像素的场景里，AABB 只盖住左上角一小块 ⇒
        //   Godot 把整个 MultiMesh 一次裁掉 ⇒ **画面上什么都没有、而且不报任何错**
        //   （这类「无报错的全黑」只能靠截图发现）。
        //   AABB 的作用是**跳过 O(N) 的实例 AABB 重算**（见 RenderBridge 的注释），
        //   所以它只要「不小于真实范围」就正确；略微放大是无害的。
        WorldRect worldBounds = new WorldRect(0f, 0f, VisibleWorldWidth, VisibleWorldHeight);
        if (_sim is FactorySim boundsSim)
        {
            worldBounds = boundsSim.WorldBounds;
            GD.Print($"[cull] 世界包围盒 = ({worldBounds.LoX:F0},{worldBounds.LoY:F0}).." +
                     $"({worldBounds.HiX:F0},{worldBounds.HiY:F0})");
        }

        // 留 2 格余量并把左下角外扩，避免边缘实例恰好落在 AABB 边界上。
        const float aabbPad = FactorySim.CellSizePx * 2f;
        var cullAabb = new Aabb(
            new Vector3(worldBounds.LoX - aabbPad, worldBounds.LoY - aabbPad, -1f),
            new Vector3(worldBounds.HiX - worldBounds.LoX + aabbPad * 2f,
                        worldBounds.HiY - worldBounds.LoY + aabbPad * 2f, 2f));

        // ⚠ 默认**关掉 per-instance 颜色**。原因见 Shaders/atlas_multimesh.gdshader 的注释：
        // 走实例颜色通路时着色器里读到的 COLOR 会让结果整体透明（无报错、尺寸校验也过）。
        // 机器状态与物品品种靠不同图集格子区分，本来也不需要染色。
        bool useColors = Environment.GetEnvironmentVariable("FORGEFLOW_USE_COLORS") == "1";
        bool minimal = Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_MINIMAL") == "1";
        bool colorsOnly = Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_COLORSONLY") == "1";
        bool customOnly = Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_CUSTOMONLY") == "1";
        bool fullLayout = Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_FULL") == "1";
        bool useCustomData = !(minimal || colorsOnly);
        if (minimal || customOnly) useColors = false;

        // 剔除：默认**开启**。它是纯渲染优化（不进 StateHash、不影响模拟），
        // 且 CullViewport 没被推送过时自动不生效 ⇒ 开启是安全的。
        // FORGEFLOW_NO_CULL=1 可关掉，用于「开/关剔除必须逐位一致」的对照验证。
        if (_sim is FactorySim cullSim)
        {
            cullSim.CullEnabled = Environment.GetEnvironmentVariable("FORGEFLOW_NO_CULL") != "1";
            GD.Print($"[cull] 视口剔除 = {(cullSim.CullEnabled ? "开" : "关")}");
        }

        // 诊断：把带面滚动相位钉死。
        // ⚠ 「带面滚的是哪个轴」这条判据在相位为 0 时**两种实现画得一模一样**，
        //   所以截图验证必须钉一个非零相位（见 tools/verify_render_orientation.py）。
        if (_sim is FactorySim scrolled
            && float.TryParse(Environment.GetEnvironmentVariable("FORGEFLOW_SCROLL_PHASE"), out float phase))
        {
            scrolled.ForcedScrollPhase = phase;
            GD.Print($"[diag] 带面滚动相位已钉死为 {phase}（截图判据用）");
        }

        _bridge = new RenderBridge(ItemMultiMesh, capacity,
                                   new Vector2(FactorySim.CellSizePx, FactorySim.CellSizePx), cullAabb,
                                   useColors, useCustomData,
                                   FactorySim.AtlasCols, FactorySim.AtlasRows,
                                   FactorySim.BuildSpriteLut(_content),
                                   FactorySim.BuildSpriteSizeLut(_content));
        _bridge.Configure();

        // 机器名 label：**与压力场景共用同一份绘制器**。数据来自模拟侧发布的机器名表
        //（`FactorySim.MachineLabels`），所以这里不需要、也**不能**去读 `_layout`。
        // 只有 FactorySim 才有机器概念（BeltSim / TrivialItemSim 没有），所以其余模拟源就没有 label。
        _labels = _sim is FactorySim fs ? new MachineLabelDrawer(fs.MachineLabels) : null;

        // 把物品陈列区的静态标签交给绘制器（构建期加进去，之后只读）。
        if (_labels != null)
            foreach ((float gx, float gy, string gn) in _galleryLabels)
                _labels.AddStatic(gx, gy, gn);

        if (minimal || colorsOnly || customOnly || fullLayout)
        {
            if (ItemMultiMesh.Material != null) ItemMultiMesh.Material = null;
            GD.Print($"[diag] 布局变体：colors={useColors} customData={useCustomData} stride={_bridge.Stride} 材质=null");
        }

        // 诊断：用 Godot 的**高层 API** 写 3 个实例（绕开自己拼的裸 buffer）。
        if (Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_XFORM") == "1")
        {
            _diagXform = true;
            for (int i = 0; i < 3; i++)
            {
                multiMesh.SetInstanceTransform2D(i, new Transform2D(0f, new Vector2(400 + i * 300, 540)));
                multiMesh.SetInstanceColor(i, i == 0 ? Colors.Red : i == 1 ? Colors.Lime : Colors.Cyan);
            }
            multiMesh.VisibleInstanceCount = 3;
            GD.Print("[diag] 已用 SetInstanceTransform2D 写 3 个实例（红/绿/青），SetBuffer 路径已跳过");
            _lastReportTickTime = Time.GetTicksMsec() / 1000.0;
            return;
        }

        _runner = new SimLoopRunner(_sim);
        _runner.Start();
        SetupHud();

        GD.Print($"[skeleton] sim={_sim.GetType().Name}  capacity={capacity:N0}  itemSize={ItemSizePx}px");
        GD.Print($"[skeleton] tick={SimLoopRunner.TickRate}Hz  stride={_bridge.Stride} float/instance  " +
                 $"buffer={capacity * _bridge.Stride * 4 / 1024 / 1024}MB/frame  colors={useColors}");

        _lastReportTickTime = Time.GetTicksMsec() / 1000.0;
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;

        // autoexit 必须在 runner 判空之前判：布局验证模式下没有 runner，
        // 否则窗口会一直挂着不退出（自动化跑测会卡死）。
        if (_autoExitSec > 0 && _elapsed >= _autoExitSec)
        {
            ReportFinal();
            _runner?.Stop();
            GetTree().Quit();
            return;
        }

        // 截图（无 GUI 时验证渲染的唯一手段）。
        // 必须等 FramePostDraw 之后再取视口纹理，否则拿到的是空图或上一帧。
        if (_screenshotPath != null && !_shotTaken && _elapsed >= _screenshotAtSec)
        {
            _shotTaken = true;
            SaveScreenshotAsync(_screenshotPath);
        }

        if (_runner == null || _sim == null) return;
        if (_diagXform) return;   // 诊断模式：不动 MultiMesh，看高层 API 写的那 3 个实例

        // ① 把「现在能看见哪一块世界」推给 Sim 线程（**必须在认领快照之前**：
        //    剔除发生在 Sim 线程的快照填充里，推晚了本 tick 就按旧视口填）。
        //    这是渲染提示，不是模拟状态 —— 不影响任何决策，见 FactorySim.CullEnabled。
        PublishViewport();

        // ⚠ 建造预览与悬停格必须在**快照认领之外**更新：
        //   认领只在新快照发布时成功（60Hz），而渲染帧率是上千 ——
        //   放在认领之后就会被 `return` 跳过 90% 以上的帧，幽灵预览跟不上鼠标。
        UpdateBuildInteraction();
        TickSequence();
        MaybeInjectSyntheticClick();
        CheckSyntheticClickResult();
        if (_sim is FactorySim verify) VerifyAutoBuild(verify);

        if (!_sim.Buffers.TryClaim(out RenderSnapshot snapshot))
            return;

        _lastSimTick = snapshot.Tick;
        _lastLiveCount = snapshot.LiveCount;
        if (_sim is FactorySim fs) _lastBeltScroll = fs.BeltScrollPhase;

        _submitClock.Restart();
        _bridge.Submit(snapshot);
        _submitClock.Stop();
        _sim.Buffers.Release();

        double ms = _submitClock.Elapsed.TotalMilliseconds;
        _submitAccumMs += ms;
        if (ms > _submitMaxMs) _submitMaxMs = ms;
        _fillSumMs += _bridge.LastFillMs;
        if (_bridge.LastFillMs > _fillMaxMs) _fillMaxMs = _bridge.LastFillMs;
        _setBufSumMs += _bridge.LastSetBufferMs;
        if (_bridge.LastSetBufferMs > _setBufMaxMs) _setBufMaxMs = _bridge.LastSetBufferMs;
        _framesSinceReport++;

        // 验证用：脚本化建造。走的是**和鼠标完全相同的那条路**（命令队列 → 模拟线程 → 快照），
        // 所以它能验证整条链路，而不只是「渲染能画东西」。
        if (!_autoBuilt && Environment.GetEnvironmentVariable("FORGEFLOW_AUTOBUILD") == "1"
            && _elapsed >= 2.0 && _sim is FactorySim auto)
        {
            _autoBuilt = true;
            AutoBuildShowcase(auto);
        }

        UpdateHud();

        if (_probe)
        {
            double nowSec = Time.GetTicksMsec() / 1000.0;
            if (nowSec - _lastReportTickTime >= 1.0)
            {
                long ticks = _runner.StepCount;
                double tickRate = (ticks - _lastReportedTick) / (nowSec - _lastReportTickTime);
                _lastReportedTick = ticks;
                _lastReportTickTime = nowSec;

                GD.Print($"[probe] fps={Engine.GetFramesPerSecond():F0}  simTick={ticks,8} ({tickRate:F1}/s)  " +
                         $"submit avg={_submitAccumMs / _framesSinceReport:F3}ms max={_submitMaxMs:F3}ms  " +
                         // 快照填充就是「渲染侧预算」，和 submit/setbuf 是**三笔不同的钱**
                         // （Sim 线程 / 桥接托管循环 / 引擎上传），所以并排打出来。
                         // ⚠ 这两个累加量必须打出来：只累加不打印等于白算。
                         $"快照填充 avg={_fillSumMs / _framesSinceReport:F3}ms max={_fillMaxMs:F3}ms  " +
                         $"setbuf avg={_setBufSumMs / _framesSinceReport:F4} max={_setBufMaxMs:F3}ms  " +
                         $"interop={_bridge.InteropCallsLastFrame}  visible={snapshot.LiveCount:N0}");

                _submitAccumMs = 0;
                _submitMaxMs = 0;
                _fillSumMs = 0;
                _fillMaxMs = 0;
                _setBufSumMs = 0;
                _setBufMaxMs = 0;
                _framesSinceReport = 0;
            }
        }
    }

    public override void _ExitTree()
    {
        _runner?.Stop();
        _runner = null;
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// 按环境变量选择模拟实现。
    /// 三者都只产出 <see cref="RenderSnapshot"/>，所以 RenderBridge 完全不知道差别 ——
    /// 这就是「领域模型零框架/零算法依赖」那层接口的实际价值。
    /// </summary>
    private ISimSource CreateSim()
    {
        string kind = Environment.GetEnvironmentVariable("FORGEFLOW_SIM") ?? "factory";

        switch (kind)
        {
            case "trivial":
                return new TrivialItemSim(InstanceCount, speed: ItemSpeed);

            case "belt":
                return new BeltSim(
                    EnvInt("FORGEFLOW_BELTS", 14),
                    EnvIntOrNull("FORGEFLOW_BELT_CELLS"),
                    EnvInt("FORGEFLOW_BELT_SPEED", 4),
                    typeCount: 8,
                    insertEveryTicks: EnvInt("FORGEFLOW_BELT_FEED", 8));

            default:
                // 验证用：一条 L 形带，用来在游戏里核对拐角铺瓦
                if (Environment.GetEnvironmentVariable("FORGEFLOW_DEMO_CORNER") == "1")
                {
                    return new FactorySim(
                        FactoryLayout.BuildCornerDemo(EnvInt("FORGEFLOW_WORK_TICKS", 60)),
                        speedSub: EnvInt("FORGEFLOW_BELT_SPEED", 2),
                        unitSizePx: ItemSizePx,
                        content: _content);
                }

                // 空地图开局：玩家从零建。这是「从演示走向游戏」的形态。
                // 陈列区照样要有 —— 它和玩家建的东西互不干扰（在 (2,32) 往下，演示布局之外）。
                if (Environment.GetEnvironmentVariable("FORGEFLOW_EMPTY") == "1")
                    return WithItemGallery(new FactoryLayout());

                FactoryLayout demo = FactoryLayout.BuildDemo(
                    EnvInt("FORGEFLOW_CHAINS", 6),
                    EnvInt("FORGEFLOW_STAGES", 3),
                    EnvIntOrNull("FORGEFLOW_BELT_CELLS") ?? 20,
                    EnvInt("FORGEFLOW_WORK_TICKS", 60),
                    EnvInt("FORGEFLOW_WORK_TICKS", 60),
                    EnvInt("FORGEFLOW_TYPES", 8));

                // **交错高低轨展示区**（主场景里要有一段复杂的、交错的高低轨道做示例）。
                //
                // ⚠ 必须加在 WithItemGallery **之前**：陈列区是"从 (1,1) 起找第一块整块空地"，
                //   先加展示区它才会绕开；反过来的话展示区会压在陈列带/标签上（那种重叠
                //   画面上是"展品的名字指到了别人的带上"，很难查）。
                // 位置 (2,24)：演示链行在 y = 2/7/12/17/22/27，所以第 23..26 行是**完整空着**的一条带；
                //   展示区占 3 行（24/25/26）⇒ 上下各留一行，既不压链行也不出屏。
                FactoryLayout.TrackInterleaveDemo track =
                    FactoryLayout.AddTrackInterleaveDemo(demo, _content, 2, 24);
                GD.Print($"[demo] 交错高低轨：低轨带 #{track.LowBelt} / 高轨带 #{track.HighBelt}，" +
                         $"在 ({track.Crossing.X},{track.Crossing.Y}) 同格交错；" +
                         $"熔炉 @({track.Furnace.X},{track.Furnace.Y}) 的输入/输出与出货机 @({track.Shipper.X},{track.Shipper.Y}) " +
                         $"**全部靠显式绑定**（几何接不上 —— 它们贴在带的中段）");
                return WithItemGallery(demo);
        }
    }

    /// <summary>
    /// 用给定布局建 sim，并**加上物品陈列区**。
    ///
    /// ⚠ 陈列带必须在构造 <c>FactorySim</c> **之前**进布局 —— <c>PrimeBelts</c> 直接改带的内容，
    ///   只能在模拟线程启动前调。这就是为什么陈列区不能像其他展示内容那样发建造命令。
    ///   两种开局（空地图 / BuildDemo）都走这里，否则跑测用的空地图分支就没有陈列区。
    /// </summary>
    private FactorySim WithItemGallery(FactoryLayout layout)
    {
        const int galCells = 6, galSpacing = 2, galColumns = 4;

        // ⚠ 陈列区必须摆在**确定空着**的地方，**不能写死坐标**（如 (32,3)）。
        //   演示布局（BuildDemo）把链行排在 y = 2,7,12,17,22,27、带一直铺到 x = 42，
        //   而写死的列区间正好横跨第 7 行的那条带 ⇒ **两条带抢同一批格子**。
        //   症状不是「画不出来」而是「画出来的是别人的」：那条演示带的物品出现在陈列区底下
        //   （标签写着铁板、跑过去的却是铁矿/铜矿），陈列区自己那条带反而没有可用格子。
        //   教训：**位置绝不能靠眼睛估** —— 「看着在画面里」不等于与别的布局不打架，
        //   后者是几何问题，那就得查占用位图。
        var occupancy = new OccupancyGrid(256, 256);
        occupancy.Rebuild(layout);
        (int galX, int galY) = FindFreeGalleryOrigin(
            occupancy, _content.Items.Count, galCells, galSpacing, galColumns);
        if (galX < 0)
            GD.PushError("[gallery] 占用位图里找不到放得下陈列区的空地 —— 陈列区不会出现在画面上");

        _galleryFirstBelt = layout.AddItemGallery(
            galX, galY, _content.Items.Count, galCells, galSpacing, galColumns);
        _galleryLabels.Clear();
        int pitchX = FactoryLayout.GalleryColumnPitch(galCells);
        for (int i = 0; i < _content.Items.Count; i++)
        {
            ItemDef it = _content.Items[i];
            // 标签放在那条短带**左端**上方 —— 让名字正指着它（网格排布也要照同一套公式算）
            (float lx, float ly) = FactoryLayout.GridToWorld(new GridPos(
                galX + (i % galColumns) * pitchX, galY + (i / galColumns) * galSpacing));
            _galleryLabels.Add((lx, ly, it.Name));
        }

        var sim = new FactorySim(layout,
            speedSub: EnvInt("FORGEFLOW_BELT_SPEED", 2),
            unitSizePx: ItemSizePx,
            content: _content);

        // 逐条带指定装哪种物品（顺序 = 内容表顺序 = 陈列顺序），**只灌陈列那一段** ——
        // 不限定范围的话会把演示布局的产线也一起灌满，那是另一回事。
        //
        // ⚠ `PrimeBelts` 的 `beltTypes` 下标是**相对 onlyFromBeltIndex** 的（见那里的注释）。
        //   这条等式（数组长度 == 这一段带的条数）就是契约 —— 对不上时后面的带会**静默**
        //   吃 fallbackType（七种物品全长一样就是这么来的），所以在发出去之前先自查一次。
        int galleryBelts = layout.BeltCount - _galleryFirstBelt;
        if (galleryBelts != _content.Items.Count)
            GD.PushError($"[gallery] 陈列带 {galleryBelts} 条 vs 物品 {_content.Items.Count} 种 —— " +
                         "PrimeBelts 按相对下标取类型，对不上会静默退化成同一种物品");

        var types = new ushort[_content.Items.Count];
        for (int i = 0; i < types.Length; i++) types[i] = (ushort)_content.Items[i].Id;
        long n = sim.PrimeBelts(types, 1, 100, onlyFromBeltIndex: _galleryFirstBelt);
        GD.Print($"[gallery] 物品陈列区：{_content.Items.Count} 种物品 × {galCells} 格，原点 ({galX},{galY})，" +
                 $"带 id {_galleryFirstBelt}..{layout.BeltCount - 1}，已灌 {n} 件（灌满即静止）");
        return sim;
    }

    /// <summary>
    /// 给物品陈列区找一块**全空**的地。
    ///
    /// 判据有三条，缺一条就会出现「看得见但不对」的那种毛病：
    ///   ① **带格要空**（不与任何机器/带/管道抢格子 —— 抢了就会画成别人的物品）；
    ///   ② **带上方那一行也要空**：每件展品的名字标签画在带的上一格，压到别人身上就白写了；
    ///   ③ **别摆到右侧 HUD 面板底下**：面板是不透明的，摆那里等于没摆
    ///      （实测面板左边缘在屏幕 x≈1361 ⇒ 默认取景下约是第 40 格）。
    ///
    /// 先在**默认取景内**（格 0..58 × 0..30，见主场景相机初始位置）按 y 再 x 扫，
    /// 找不到才放开到整张位图 —— 宁可位置难看，也绝不与已有内容重叠。
    /// 返回 (-1,-1) = 真找不到（调用方报错，而不是硬塞一个会重叠的位置）。
    /// </summary>
    private static (int X, int Y) FindFreeGalleryOrigin(
        OccupancyGrid grid, int count, int cells, int spacing, int columns)
    {
        if (count <= 0) return (1, 1);

        int pitchX = FactoryLayout.GalleryColumnPitch(cells);
        int rows = (count + columns - 1) / columns;
        int blockW = (columns - 1) * pitchX + cells;
        int blockH = (rows - 1) * spacing + 1;

        for (int pass = 0; pass < 2; pass++)
        {
            // pass 0：默认取景内、且避开右侧 HUD 面板（留 1 格余量 ⇒ x 上界 39）
            int maxX = pass == 0 ? HudReservedCellX - 1 : grid.Width - 1;
            int maxY = pass == 0 ? 30 : grid.Height - 1;
            for (int y = 1; y + blockH - 1 <= maxY; y++)
            {
                for (int x = 1; x + blockW - 1 <= maxX; x++)
                {
                    if (BlockIsFree(grid, x, y, count, cells, spacing, columns, pitchX))
                        return (x, y);
                }
            }
        }
        return (-1, -1);
    }

    /// <summary>
    /// 默认取景下「右侧 HUD 面板」左边缘所在的格号（实测：面板屏幕 x≈1361 ⇒ 世界 x≈1301
    /// ⇒ 1301/32≈40.7）。**不是可以随手改的数**：面板变宽/挪位置就得重量一次并改这里。
    /// 只是「尽量别摆那儿」的软约束（pass 0 用），pass 1 会放开。
    /// </summary>
    private const int HudReservedCellX = 40;

    /// <summary>网格状的陈列区（<paramref name="count"/> 条带、每行 <paramref name="columns"/> 条）
    /// 是不是整块都空着。每条带除了自己那 <paramref name="cells"/> 格，还要留出**上一行**给标签。</summary>
    private static bool BlockIsFree(OccupancyGrid grid, int x, int y, int count, int cells,
                                    int spacing, int columns, int pitchX)
    {
        for (int i = 0; i < count; i++)
        {
            int ox = x + (i % columns) * pitchX;
            int oy = y + (i / columns) * spacing;
            for (int k = 0; k < cells; k++)
            {
                if (!grid.IsFree(new GridPos(ox + k, oy))) return false;         // 带格
                if (oy > 0 && !grid.IsFree(new GridPos(ox + k, oy - 1))) return false;  // 标签行
            }
        }
        return true;
    }

    /// <summary>物品陈列区的第一条带 id（<see cref="PrimeBelts"/> 只灌这一段）。</summary>
    private int _galleryFirstBelt = -1;

    /// <summary>物品陈列区的标签（构建期确定，之后只读）。</summary>
    private readonly List<(float X, float Y, string Name)> _galleryLabels = new();

    private static int EnvInt(string name, int fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(v) && int.TryParse(v, out int n) && n > 0 ? n : fallback;
    }

    private static int? EnvIntOrNull(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(v) && int.TryParse(v, out int n) && n > 0 ? n : null;
    }

    /// <summary>
    /// 布局验证模式：不启动模拟线程，手工提交**一个**已知位置与尺寸的实例。
    /// 期望：世界坐标 (960, 540) 处出现 200x200 青色方块。
    /// </summary>
    private void RunLayoutProbe(MultiMesh multiMesh)
    {
        const int count = 1;
        const float probeSize = 200f;
        float px = TrivialItemSim.DefaultWorldWidth * 0.5f;
        float py = TrivialItemSim.DefaultWorldHeight * 0.5f;

        var bridge = new RenderBridge(
            ItemMultiMesh, count,
            new Vector2(FactorySim.CellSizePx, FactorySim.CellSizePx),
            new Aabb(new Vector3(0, 0, -1),
                     new Vector3(TrivialItemSim.DefaultWorldWidth, TrivialItemSim.DefaultWorldHeight, 2)),
            // 探针要一个 200×200 的方块：尺寸表按「格」给，所以这里填 200/32。
            spriteLut: null,
            sizeLut: Filled(probeSize / FactorySim.CellSizePx));
        bridge.Configure();

        var snap = new RenderSnapshot(count);
        snap.Xy[0] = px;
        snap.Xy[1] = py;
        snap.SpriteId[0] = 0;
        snap.Tint[0] = 0xFF00FFFF;   // 青色不透明
        snap.LiveCount = count;
        snap.Tick = 0;
        bridge.Submit(snap);

        GD.Print($"[layout-probe] 已提交 1 个实例，期望世界坐标 ({px}, {py}) 处出现 {probeSize}x{probeSize} 青色方块");
    }

    /// <summary>造一张「所有精灵都是同一个尺寸（单位=格）」的尺寸表 —— 布局验证模式用。</summary>
    private static float[] Filled(float cells)
    {
        var lut = new float[FactorySim.AtlasCells * 2];
        for (int i = 0; i < FactorySim.AtlasCells; i++) { lut[i * 2] = cells; lut[i * 2 + 1] = cells; }
        return lut;
    }

    /// <summary>抓一张视口截图。**必须在 FramePostDraw 之后**取纹理，否则会拿到空图。</summary>
    private async void SaveScreenshotAsync(string path)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();
        if (img == null)
        {
            GD.PushError("[screenshot] 视口纹理为空");
            return;
        }
        Error err = img.SavePng(path);
        GD.Print($"[screenshot] {path}  err={err}  {img.GetWidth()}x{img.GetHeight()}  " +
                 $"simTick={_lastSimTick}  beltScrollPhase={_lastBeltScroll:F4}");
    }

    /// <summary>
    /// 解析 HUD 节点（场景里预建）。缺任何一个就整体关掉并告警 ——
    /// 宁可没有 HUD，也不要半个 HUD 或者静默的 NullReference。
    /// </summary>
    private void SetupHud()
    {
        _hud = GetNodeOrNull<CanvasLayer>("Hud");
        if (_hud != null)
        {
            _hudStatus = _hud.GetNodeOrNull<Label>("Panel/VBox/LineStatus");
            _hudThroughput = _hud.GetNodeOrNull<Label>("Panel/VBox/LineThroughput");
            _hudMachines = _hud.GetNodeOrNull<Label>("Panel/VBox/LineMachines");
            _hudInstances = _hud.GetNodeOrNull<Label>("Panel/VBox/LineInstances");
            _hudPacing = _hud.GetNodeOrNull<Label>("Panel/VBox/LinePacing");
            _hudHint = _hud.GetNodeOrNull<Label>("Panel/VBox/LineHint");
            _hudBuild = _hud.GetNodeOrNull<Label>("Panel/VBox/LineBuild");
            _hudInfo = _hud.GetNodeOrNull<Label>("Panel/VBox/LineInfo");
            _hudItems = _hud.GetNodeOrNull<Label>("Panel/VBox/LineItems");
            _toolbarTitle = _hud.GetNodeOrNull<Label>("Toolbar/VBox/ToolTitle");

            // **物品图例**：把内容表里的全部物品列出来（名字 + 图集格号）。
            // 直接读内容表 ⇒ 加一种物品只改内容表，这里和场景都不用动。
            if (_hudItems != null)
            {
                var sb = new System.Text.StringBuilder("物品：");
                foreach (ItemDef it in _content.Items)
                    sb.Append($"  {it.Name}(#{it.Id} 格{it.IconCell})");
                _hudItems.Text = sb.ToString();
            }
        }

        if (_hud == null || _hudStatus == null || _hudThroughput == null || _hudMachines == null ||
            _hudInstances == null || _hudPacing == null || _hudHint == null || _hudBuild == null
            || _hudInfo == null)
        {
            GD.PushWarning("HUD 节点不齐（Hud/Panel/VBox/Line*），已关闭 HUD。" +
                           "请在 Scenes/Main.tscn 里补齐 —— 工程约定是节点在场景里预建。");
            _hud = null;
            return;
        }

        if (Environment.GetEnvironmentVariable("FORGEFLOW_HUD") == "0")
        {
            _hud.Visible = false;
            GD.Print("[hud] 已按 FORGEFLOW_HUD=0 隐藏");
            return;
        }

        _hudHint.Text = "相机：WASD 平移 · 滚轮缩放 · 中键拖拽 · Home 复位    " +
                        "鼠标：左键=放置/铺带/单击选中 · 右键单击=拆一个 · 右键拖=框选 · X/Delete=拆选中";

        SetupBuildInteraction();
        GD.Print($"[hud] 已就绪（5 Hz 刷新）：内容表 {_content.Buildables.Count} 种可建造项");
    }

    /// <summary>接上鼠标、工具栏按钮、建造幽灵与蓝图预览层。节点仍是场景预建的。</summary>
    private void SetupBuildInteraction()
    {
        _ghost = GetNodeOrNull<Sprite2D>("BuildGhost");
        if (_ghost == null)
        {
            GD.PushWarning("没有 BuildGhost 节点，建造预览将不可见（功能仍可用）。");
        }
        else
        {
            // ⚠ 必须显式打开 RegionEnabled —— 否则设了 RegionRect 也不生效，
            //   精灵会把**整张图集**（320×3776）铺在一格上。这就是「蓝图显示有问题」的根因。
            _ghost.RegionEnabled = true;
            _ghost.Centered = true;
        }

        _selection = GetNodeOrNull<ColorRect>("SelectionRect");

        // 预览矩形池：场景里预建 PreviewRect00..02（数量够用即可，L 形拖拽最多 2 段）
        var rects = new List<ColorRect>();
        for (int i = 0; ; i++)
        {
            ColorRect? r = GetNodeOrNull<ColorRect>($"PreviewRect{i:00}");
            if (r == null) break;
            r.Visible = false;
            rects.Add(r);
        }
        _previewRects = rects.ToArray();
        if (_previewRects.Length == 0)
            GD.PushWarning("没有 PreviewRect00.. 节点 —— 铺带的预计位置与拆除高亮都不会显示。" +
                           "节点必须在 Scenes/Main.tscn 里预建。");

        // ── 工具栏：按固定名绑定场景里预建的「图标按钮 + 名称/作用标签」──
        // 名称与作用都取自内容表（`ContentDb.Name` / `FunctionOf`），所以「加一种建筑」= 内容表一行
        // + 场景里一对「按钮 + 标签」，脚本不用改。
        int n = _content.Buildables.Count;
        _toolButtons = new Button[n];
        _toolLabels = new Label?[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;   // ⚠ for 循环变量只有一个实例，闭包必须捕获副本
            BuildableDef def = _content.Buildables[i];

            Button? btn = _hud?.GetNodeOrNull<Button>($"Toolbar/VBox/Grid/ToolButton{i:00}");
            if (btn != null)
            {
                _toolButtons[i] = btn;
                btn.TooltipText = $"{i + 1}  {def.Name} —— {_content.FunctionOf(def)}";
                btn.Pressed += () => SelectBuildable(idx);
            }

            Label? lbl = _hud?.GetNodeOrNull<Label>($"Toolbar/VBox/Grid/ToolLabel{i:00}");
            if (lbl != null)
            {
                _toolLabels[i] = lbl;
                // 两行：**名称** + **作用**。作用优先从配方推（「炼铁板：铁矿 → 铁板」），
                // 无配方的机器用内容表里的 Note —— 两处都不在这里手写，避免两份真相。
                lbl.Text = $"{def.Name}\n{_content.FunctionOf(def)}";
            }
        }
        int bound = 0;
        foreach (Button? b in _toolButtons) if (b != null) bound++;
        if (bound != n)
            GD.PushWarning($"工具栏按钮只绑定了 {bound}/{n} 个 —— 场景里 ToolButton00..{n - 1:00} 可能不全。" +
                           "（点选会缺项，数字键仍然可用）");
        int labelled = 0;
        foreach (Label? l in _toolLabels) if (l != null) labelled++;
        if (labelled != n)
            GD.PushWarning($"工具栏只绑定了 {labelled}/{n} 个说明标签（ToolLabel00..{n - 1:00}）——" +
                           "菜单里会缺「名称 / 作用」。请在 Scenes/Main.tscn 里补齐。");

        // ── 铺带按钮：它**不是一种可建造项**（带不是机器），所以单独接线、单独高亮 ──
        _beltButton = _hud?.GetNodeOrNull<Button>("Toolbar/VBox/Grid/ToolButtonBelt");
        if (_beltButton == null)
        {
            GD.PushWarning("工具栏里没有 ToolButtonBelt —— 铺带只能按 B 键（菜单里点不到），" +
                           "请在 Scenes/Main.tscn 里补上这个按钮与图标。");
        }
        else
        {
            _beltButton.Pressed += () => SetState(BuildState.BeltIdle);
        }
        Label? beltLabel = _hud?.GetNodeOrNull<Label>("Toolbar/VBox/Grid/ToolLabelBelt");
        if (beltLabel != null)
            beltLabel.Text = "铺带\n拖拽铺设，自动拐角（Shift 换拐弯顺序）";

        // ── 蓝图预览层：复用场景里的 ProbeMultiMesh（另配 custom_data 与图集材质）──
        // ⚠ 诊断模式（FORGEFLOW_DIAG_MULTIMESH）会自己写 1 个放大实例来隔离渲染问题，
        //   和预览抢同一个节点；这时就不接预览层。
        bool diagProbe = Environment.GetEnvironmentVariable("FORGEFLOW_DIAG_MULTIMESH") == "1";
        MultiMeshInstance2D? preview = GetNodeOrNull<MultiMeshInstance2D>("ProbeMultiMesh");
        if (!diagProbe && preview?.Multimesh != null && preview.Material != null)
        {
            try
            {
                _previewBridge = new RenderBridge(preview, BlueprintPreviewCapacity,
                    new Vector2(FactorySim.CellSizePx, FactorySim.CellSizePx),
                    new Aabb(new Vector3(0f, 0f, -1f), new Vector3(VisibleWorldWidth, VisibleWorldHeight, 2f)),
                    useColors: false, useCustomData: true,
                    FactorySim.AtlasCols, FactorySim.AtlasRows,
                    FactorySim.BuildSpriteLut(_content), FactorySim.BuildSpriteSizeLut(_content));
                _previewBridge.Configure();
                _previewSnapshot = new RenderSnapshot(BlueprintPreviewCapacity);
                preview.Visible = false;
                GD.Print("[blueprint] 预览层已接上 ProbeMultiMesh");
            }
            catch (Exception e)
            {
                GD.PushWarning($"蓝图预览层接不上（{e.Message}），粘贴时只显示左上角格。");
                _previewBridge = null;
            }
        }
        else
        {
            GD.PushWarning("ProbeMultiMesh 缺 MultiMesh 或材质 —— 蓝图预览退化为单格幽灵。");
        }

        string? seq = Environment.GetEnvironmentVariable("FORGEFLOW_SIMSEQ");
        if (!string.IsNullOrEmpty(seq))
        {
            _seq = seq.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _seqNextAt = 3.0;
            GD.Print($"[seq] 脚本化输入 {_seq.Length} 步：{string.Join(" | ", _seq)}");
        }

        // 环境变量可以先给一个默认工具，方便脚本化验证
        string? t = Environment.GetEnvironmentVariable("FORGEFLOW_TOOL");
        if (!string.IsNullOrEmpty(t)) ApplyToolSpec(t);

        RefreshToolbarHighlight();
        SetupTrackPicker();
        GD.Print($"[build] 当前工具 = {ToolName()}（左键放置，右键拆除；铺带按住拖拽；R 旋转；T 换高/低轨）");
    }

    /// <summary>
    /// 接上**轨道选择器**（Scenes/Main.tscn 里的 `Hud/TrackPicker`）：一台机器绑哪条轨道。
    ///
    /// 复用点（"支持该功能复用"）：
    ///   · **同一份候选列表 + 同一组按钮**同时服务"输入轨"和"输出轨"两个角色 ——
    ///     加第三个角色只需要多一个按钮，不用再来一个面板；
    ///   · 选中列表里某一条时，世界里**复用「选中态高亮」**把它整段提亮（见 AppendBeltTiles），
    ///     所以"我选的是哪条"和"这条从哪走到哪"是一眼可见的；
    ///   · 候选来自 <see cref="FactoryLayout.ReachableBelts"/>，而它与几何邻接**同口径** ——
    ///     不会出现"UI 里选得到、模拟里接不上"。
    /// 节点缺了就只警告（轨道选择仍可用快捷键之外的方式：先绑定的机器继续按几何走）。
    /// </summary>
    private void SetupTrackPicker()
    {
        _trackPicker = _hud?.GetNodeOrNull<PanelContainer>("TrackPicker");
        _trackList = _hud?.GetNodeOrNull<ItemList>("TrackPicker/VBox/TrackList");
        _trackTitle = _hud?.GetNodeOrNull<Label>("TrackPicker/VBox/Title");
        _btnBindIn = _hud?.GetNodeOrNull<Button>("TrackPicker/VBox/Buttons/BtnBindIn");
        _btnBindOut = _hud?.GetNodeOrNull<Button>("TrackPicker/VBox/Buttons/BtnBindOut");
        _btnClear = _hud?.GetNodeOrNull<Button>("TrackPicker/VBox/Buttons/BtnClear");
        _btnClose = _hud?.GetNodeOrNull<Button>("TrackPicker/VBox/Buttons/BtnClose");

        if (_trackPicker == null || _trackList == null || _trackTitle == null ||
            _btnBindIn == null || _btnBindOut == null || _btnClear == null || _btnClose == null)
        {
            GD.PushWarning("Hud/TrackPicker 节点不全 —— 轨道绑定只能靠几何推导。" +
                           "请在 Scenes/Main.tscn 里补齐（工程约定：节点预建）。");
            _trackPicker = null;
            return;
        }

        _trackPicker.Visible = false;
        _trackList.ItemSelected += OnPickerRowSelected;
        _btnBindIn.Pressed += () => SubmitPickerBinding(TrackRole.Input);
        _btnBindOut.Pressed += () => SubmitPickerBinding(TrackRole.Output);
        _btnClear.Pressed += () => SubmitPickerBinding(TrackRole.Clear);
        _btnClose.Pressed += () =>
        {
            // 「关闭」= 取消选中：选择器跟着选中走，所以清掉选中它就自己收起来了。
            _selCell = null;
            _hasSelection = false;
            _pickerBelt = -1;
        };
        GD.Print("[track] 轨道选择器已接上（点机器 → 选一条轨道 → 绑为输入/输出）");
    }

    /// <summary>当前单击选中的那台机器；没选机器（或正在框选）返回 -1。</summary>
    private int SelectedMachine(FactorySim f)
    {
        if (_hasSelection || _selCell is not { } c) return -1;
        return f.Occupancy.MachineAt(c);     // 多格机器的每一格都记着同一个 id，点哪格都行
    }

    /// <summary>
    /// 刷新轨道选择器（它只服务「当前选中的那台机器」）。
    ///
    /// 候选**每帧重算**而不是缓存：候选是布局的函数（拆了带、接了新带、机器挪了都会变），
    /// 缓存下来就会出现「列表里那条带其实已经没了」。主场景的带数量很小，这点遍历不值一提。
    /// 但**条目**只在真正变化时重建 —— 每帧 `Clear()` 会把玩家的选中态抹掉（列表会闪）。
    /// </summary>
    private void RefreshTrackPicker(FactorySim f)
    {
        if (_trackPicker == null || _trackList == null) return;

        int machine = SelectedMachine(f);
        if (machine < 0)
        {
            // 没选机器 ⇒ 收起。**这是它不吃鼠标的前提**：一个常驻的 Control 会把
            // 它盖住的那一片鼠标点击全吃掉（PanelContainer 一常驻就会这样）。
            if (_trackPicker.Visible)
            {
                _trackPicker.Visible = false;
                _trackList.Clear();
                _pickerBelts.Clear();
                _pickerMachineCell = null;
                _pickerBelt = -1;
                _pickerMachine = -1;
                _pickerRevision = -1;
            }
            return;
        }

        MachineDef mdef = f.Layout.MachineAt(machine)!;
        _pickerMachineCell = mdef.Pos;

        // ⚠ **候选表不要每帧重算**：`ReachableBelts` 要遍历全部带格（主场景几百格无所谓，
        //   但玩家真把厂子建大了就是每帧几十万次）。只在「换了机器」或「拓扑变了」时重算 ——
        //   绑定、建造、拆除、撤销都会让 `TopologyRevision` 变，所以绑定状态不会显示错。
        //   而条目里的「带 N 件」会随时间漂，所以**文本**按 HUD 的频率（5Hz）刷一次。
        bool recompute = machine != _pickerMachine || f.TopologyRevision != _pickerRevision;
        if (recompute)
        {
            _pickerMachine = machine;
            _pickerRevision = f.TopologyRevision;
            _pickerBelts.Clear();
            _pickerBelts.AddRange(f.Layout.ReachableBelts(machine));
            RebuildPickerRows(f, machine);
            _pickerRowsAt = _elapsed;
        }
        else if (_elapsed - _pickerRowsAt >= HudIntervalSec)
        {
            _pickerRowsAt = _elapsed;
            RebuildPickerRows(f, machine);
        }

        _trackPicker.Visible = true;
        if (_trackTitle != null)
        {
            // 标题写清三件事：**这台机器需要哪些角色**（种类决定）、当前各自接了什么、还有没有缺。
            // 机器必须接线才能用 —— 所以"还缺什么"必须是面板上第一眼看到的东西。
            _trackTitle.Text = $"轨道绑定 · 机器 #{machine} {BuildableName(mdef.SpriteCell)} " +
                               $"@({mdef.Pos.X},{mdef.Pos.Y}) · {NeedText(mdef)}\n" +
                               $"输入 {BoundText(mdef.InBeltBindings)} · 输出 {BoundText(mdef.OutBeltBindings)}" +
                               (mdef.IsWired ? "" : "   ⚠ 未接线：机器不会工作");
        }
    }

    /// <summary>这台机器需要哪些角色（由种类决定，规则在 Sim 侧的 <see cref="MachineWiring"/>）。</summary>
    private static string NeedText(MachineDef mdef)
    {
        bool needIn = MachineWiring.NeedsInput(mdef.Kind);
        bool needOut = MachineWiring.NeedsOutput(mdef.Kind);
        if (!needIn && !needOut) return "不需要轨道（流体机器）";
        if (needIn && needOut) return "需要 输入轨 + 输出轨";
        return needIn ? "需要 输入轨" : "需要 输出轨";
    }

    private static string BoundText(int[] belts)
        => belts.Length == 0 ? "未绑定" : string.Join(",", Array.ConvertAll(belts, b => $"#{b}"));

    /// <summary>按当前候选重建列表条目（选中态尽量保留）。</summary>
    private void RebuildPickerRows(FactorySim f, int machine)
    {
        if (_trackList == null) return;
        MachineDef mdef = f.Layout.MachineAt(machine)!;
        _trackList.Clear();

        foreach (int b in _pickerBelts)
        {
            BeltDef? bd = f.Layout.BeltAt(b);
            if (bd == null) continue;
            GridPos a = bd.Cells[0], z = bd.Cells[^1];
            // ⚠ 没绑定时写**「未绑定」**，不要写一个破折号：`—`（U+2014）在游戏字体里
            //   看起来就是汉字「一」，玩家读成「当前：一」完全不知道什么意思。
            string role = mdef.IsBoundInput(b) ? "输入"
                        : mdef.IsBoundOutput(b) ? "输出" : "未绑定";
            _trackList.AddItem($"带 #{b} {LevelName(bd.Level)} {bd.Cells.Length} 格 " +
                               $"({a.X},{a.Y})→({z.X},{z.Y}) · 带 {f.BeltItems(b)} 件 · 当前:{role}");
        }

        int row = _pickerBelts.IndexOf(_pickerBelt);
        if (row < 0 && _pickerBelts.Count > 0) row = 0;
        _pickerBelt = row >= 0 && row < _pickerBelts.Count ? _pickerBelts[row] : -1;
        if (row >= 0) _trackList.Select(row);     // Select 不会发 ItemSelected ⇒ 不会递归
    }

    private void OnPickerRowSelected(long index)
    {
        if (_trackList == null) return;
        if (index < 0 || index >= _pickerBelts.Count) return;
        _pickerBelt = _pickerBelts[(int)index];
        GD.Print($"[track] 选中 带 #{_pickerBelt}（世界里已提亮它整段）");
    }

    /// <summary>把列表里选中的那条轨道绑成输入 / 输出，或解除绑定。</summary>
    private void SubmitPickerBinding(TrackRole role)
    {
        if (_pickerMachineCell is not { } cell)
        {
            GD.Print("[track] 先在世界里左键单击一台机器，再选轨道");
            return;
        }
        if (role != TrackRole.Clear && _pickerBelt < 0)
        {
            GD.Print("[track] 先在列表里点一条轨道");
            return;
        }
        Submit(new BindMachineTrackCommand(cell, role, _pickerBelt));
    }

    private const int BlueprintPreviewCapacity = 512;

    /// <summary>把 "iron-mine" / "belt" / "none" / 数字 解析成状态（跑测与 `FORGEFLOW_TOOL` 用的入口）。</summary>
    private bool ApplyToolSpec(string spec)
    {
        switch (spec.ToLowerInvariant())
        {
            case "belt" or "b":
                SetState(BuildState.BeltIdle); return true;
            case "none" or "off" or "select":
                SetState(BuildState.Idle); return true;
        }
        if (int.TryParse(spec, out int num) && num >= 1 && num <= _content.Buildables.Count)
        {
            SelectBuildable(num - 1);
            return true;
        }
        for (int i = 0; i < _content.Buildables.Count; i++)
        {
            if (string.Equals(_content.Buildables[i].Id, spec, StringComparison.OrdinalIgnoreCase))
            {
                SelectBuildable(i);
                return true;
            }
        }
        GD.PushError($"[build] 认不出工具 '{spec}'（可用：内容表的 id、belt、none）");
        return false;
    }

    private void SelectBuildable(int index)
    {
        if ((uint)index >= (uint)_content.Buildables.Count) return;
        _buildableIndex = index;
        SetState(BuildState.Placing);     // 进入动作里会把朝向归零（玩家的要求）
    }

    private void RefreshToolbarHighlight()
    {
        for (int i = 0; i < _toolButtons.Length; i++)
        {
            Button? b = _toolButtons[i];
            if (b == null) continue;
            bool on = _state == BuildState.Placing && i == _buildableIndex;
            // ⚠ 只用 Modulate 表示选中，**不要动 Disabled**：
            //   Button 在 Disabled 状态切换时会发出 pressed 信号，
            //   于是启动时工具栏会「自己」选中某一项（实测选中了长臂机械臂）——
            //   症状就是「一进游戏光标下就挂着一个幽灵，而且那不是我选的工具」。
            b.Modulate = on ? new Color(1f, 1f, 1f) : new Color(0.72f, 0.75f, 0.82f);
        }

        if (_beltButton != null)
        {
            bool beltOn = _state is BuildState.BeltIdle or BuildState.BeltDrag;
            _beltButton.Modulate = beltOn ? new Color(1f, 1f, 1f) : new Color(0.72f, 0.75f, 0.82f);
        }
    }

    private string ToolName()
    {
        if (_state == BuildState.Paste)
        {
            int n = _sim is FactorySim f ? f.ClipboardMachines + f.ClipboardBelts + f.ClipboardPipes : 0;
            return $"蓝图粘贴（{n} 项）";
        }
        return _state switch
        {
            BuildState.Placing => _content.Buildables[_buildableIndex].Name,
            BuildState.BeltIdle or BuildState.BeltDrag => "铺带（拖拽）",
            BuildState.BoxSelect => "框选中",
            _ => "空手（单击选中）",
        };
    }

    /// <summary>世界坐标 → 网格坐标（共用 <see cref="FactoryLayout.WorldToGrid"/>，不在这里另算一份）。</summary>
    private static GridPos WorldToGrid(Vector2 world)
        => FactoryLayout.WorldToGrid(world.X, world.Y);

    /// <summary>
    /// 验证用：**通过真实输入管线**注入一次点击（Input.ParseInputEvent）。
    /// 之所以不用直接调 Submit —— 那样就绕过了 _UnhandledInput、GUI 吞噬、坐标换算这些
    /// 真正容易出错的地方，测了等于没测。
    /// 用法：<c>FORGEFLOW_SIMCLICK="tool,x,y"</c>，例如 <c>iron-mine,30,20</c>。
    /// </summary>
    private void MaybeInjectSyntheticClick()
    {
        if (_syntheticClickDone) return;
        string? spec = Environment.GetEnvironmentVariable("FORGEFLOW_SIMCLICK");
        if (string.IsNullOrEmpty(spec)) { _syntheticClickDone = true; return; }
        if (_elapsed < 3.0) return;

        _syntheticClickDone = true;

        string[] parts = spec.Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[1], out int gx) || !int.TryParse(parts[2], out int gy))
        {
            GD.PushError($"[simclick] 格式应为 tool,x,y，收到 '{spec}'");
            return;
        }

        ApplyToolSpec(parts[0]);
        BuildState state = _state;

        var world = new Vector2(gx * FactorySim.CellSizePx + FactorySim.CellSizePx * 0.5f,
                                gy * FactorySim.CellSizePx + FactorySim.CellSizePx * 0.5f);

        // ⚠ 用 WorldToViewport 换算 —— 它与 GetGlobalMousePosition() 严格互逆。
        //   之前这里用的是 GetViewportTransform()，那条路在 _Process 里读到的变换
        //   与鼠标链路不一致，注入出来的格子会整体偏掉（症状：明明空的格子报「已占用」）。
        Vector2 screen = WorldToViewport(world);

        GD.Print($"[simclick] 工具={ToolName()}（{parts[0]}）  网格=({gx},{gy})  世界={world}  注入视口坐标={screen}");

        var cell = new GridPos(gx, gy);
        var fs = _sim as FactorySim;
        if (fs != null)
            GD.Print($"[simclick] 预检：该格可放机器={fs.Occupancy.CanPlaceMachine(cell)}  " +
                     $"可放带={fs.Occupancy.CanPlaceBelt(cell)}  该格有东西={fs.Occupancy.IsOccupied(cell)}");

        _preClickMachines = fs?.MachineCount ?? 0;

        Input.ParseInputEvent(new InputEventMouseMotion { Position = screen });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = screen,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = screen,
        });
        if (state == BuildState.Idle)
        {
            // 空手里的单击是「选中」而不是建造，所以这一对同格按下/抬起不会建出任何东西；
            // 真正的建造验证请用 FORGEFLOW_TOOL=<建筑 id> 或 SIMSEQ 的 click / drag。
            GD.Print("[simclick] 提示：当前是空手（单击只选中），要验证建造请先选一种建筑或铺带");
        }
    }

    /// <summary>合成点击之后隔一会儿核对结果（命令要等下一个 tick 才生效）。</summary>
    private void CheckSyntheticClickResult()
    {
        if (!_syntheticClickDone || _clickResultChecked) return;
        if (_elapsed < 4.0) return;
        _clickResultChecked = true;

        if (_sim is not FactorySim f) return;
        int delta = f.MachineCount - _preClickMachines;
        GD.Print($"[simclick] 结果核对：机器数 {_preClickMachines} → {f.MachineCount}（差 {delta}）  " +
                 $"接受={f.AppliedBuildCount} 拒绝={f.RejectedBuildCount}  最近={f.LastBuildResult.Outcome} {f.LastBuildResult.Detail}");
        GD.Print(delta > 0
            ? "[simclick] ✅ 放置成功 —— 鼠标 → _UnhandledInput → 命令队列 → 模拟线程整条链路通了"
            : "[simclick] ❌ 没有放下任何东西");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_sim is not FactorySim f) return;

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleKey(key);
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            _hover = CursorCell();
            _sawMotionThisFrame = true;

            // 鼠标坐标诊断（FORGEFLOW_MOUSEDIAG=1）：把「事件自带的坐标」与
            // 「此刻真实光标的位置」并排打出来。两者差得多说明这条事件是旧的
            // （鼠标在事件产生后又动了）—— 这正是不能拿事件坐标定位的原因之一。
            if (Environment.GetEnvironmentVariable("FORGEFLOW_MOUSEDIAG") == "1")
            {
                Vector2 real = WorldToViewport(GetGlobalMousePosition());
                Vector2 delta = motion.Position - real;
                string tag = delta.Length() < 1.5f ? "OK" : "STALE";
                GD.Print($"[mouse] {tag} 事件pos={motion.Position} 真实光标(视口)={real} 差={delta.Length():F1}px   " +
                         $"实际用到的格=({_hover.X},{_hover.Y})  窗口={DisplayServer.WindowGetSize()} " +
                         $"视口={GetViewportRect().Size}");
            }
            return;   // 移动事件不拦截，让相机等其他接收者也能看到
        }

        if (@event is InputEventMouseButton mb)
        {
            // ⚠ 位置一律取真实光标（理由见 CursorCell），**不看 mb.Position**
            GridPos cell = CursorCell();
            _hover = cell;

            // ── 右键：单击拆一个 / 按住拖拽 = 框选 ──────────────────────────────
            // 判据是「松开的那一格是不是就是按下的那一格」：
            //   同一格 ⇒ 单击（拆掉鼠标下那一个，不需要任何「拆除模式」）
            //   换了一格 ⇒ 框选（松开**只选中**，按 X / Delete 才真拆）
            // ⚠ 所以删除发生在**松开**时，不是按下时 —— 否则拖拽一开头就把东西拆了。
            if (mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed)
                {
                    _dragAnchor = cell;
                    _returnState = _state == BuildState.BoxSelect ? BuildState.Idle : _state;
                    SetState(BuildState.BoxSelect);
                }
                else if (_state == BuildState.BoxSelect)
                {
                    GridPos anchor = _dragAnchor;
                    SetState(_returnState);
                    if (anchor == cell)
                    {
                        Submit(new RemoveAtCommand(cell));
                    }
                    else
                    {
                        _selA = anchor;
                        _selB = cell;
                        _hasSelection = true;
                        _selCell = null;
                        // 松开的瞬间：**世界里的框与提示一起消失**（框只属于「按住中」这个动作），
                        // 但区域被记住了 —— HUD 的「选中」行会说明记住了多大一块。
                        GD.Print($"[build] 框选 ({_selA.X},{_selA.Y})..({_selB.X},{_selB.Y})：框已收起，" +
                                 $"区域仍有效 —— X 或 Delete 拆掉 · Ctrl+C 复制");
                    }
                }
                return;
            }

            if (mb.ButtonIndex != MouseButton.Left) return;

            // 左键的含义**只由当前状态决定** —— 一张表，没有组合出来的非法情况。
            switch (_state)
            {
                case BuildState.Idle:
                    if (mb.Pressed) SelectAt(cell);
                    return;

                case BuildState.BoxSelect:
                    return;    // 右键拖拽中，左键不参与

                case BuildState.Paste:
                    if (mb.Pressed) Submit(new PasteCommand(cell));
                    return;                        // 保持粘贴模式，可以连着贴

                case BuildState.Placing:
                {
                    if (!mb.Pressed) return;
                    BuildableDef def = _content.Buildables[_buildableIndex];
                    GridPos origin = OriginFor(cell, def.SizeX, def.SizeY);
                    // ⚠ 光标压在已有东西上时**改成选中**，而不是发一条注定被拒的放置命令 ——
                    //   「普通点击看信息」在任何模式下都成立，玩家不用先按 Esc 回空手。
                    //   空格才真的放。（放置被拒的反馈也就没用了：那格本来就不可能放下。）
                    if (f.Occupancy.CanPlaceMachine(origin, def.SizeX, def.SizeY)) SubmitPlace(def, origin);
                    else SelectAt(cell);
                    return;
                }

                case BuildState.BeltIdle:
                    if (!mb.Pressed) return;
                    // 带不可能从**两种层都放不下**的格子上起步（机器上、管道上）⇒ 那一格上的单击
                    // 同样是「选中看信息」。⚠ 判据是"挑层后能不能铺"而不是"这一层空不空"：
                    //   一格上有低轨、而当前层也是低轨时，**高轨那一层是空的** ⇒ 仍然可以起步
                    //   （这就是"拖着穿过已有轨道自动交错"的起点）。
                    if (!CellPlaceable(cell)) { SelectAt(cell); return; }
                    _dragAnchor = cell;
                    SetState(BuildState.BeltDrag);
                    return;

                case BuildState.BeltDrag:
                    if (mb.Pressed) return;
                    // ⚠ 先算再切：切状态会丢掉锚点（见 SetState）。
                    if (_dragAnchor == cell && !CellPlaceable(cell)) SelectAt(cell);
                    else SubmitBelt(_dragAnchor, cell);
                    SetState(BuildState.BeltIdle);
                    return;
            }
        }
    }

    /// <summary>
    /// 单击选中某一格上的东西（HUD 会显示它的信息、世界里给它一个亮一档的高亮）。
    /// 选一个**新**的会顶掉旧的；点空格子 = 取消框选但保留「选中了这格」（HUD 会说这格是空的）。
    /// </summary>
    private void SelectAt(GridPos cell)
    {
        _selCell = cell;
        _hasSelection = false;      // 单击与框选互斥：最后选的那个说了算
    }

    /// <summary>
    /// 拆掉「当前选中的东西」：框选就拆整个矩形，单击选中的就拆那一格上的东西。
    /// `X` / `Delete` 走这里 —— **没有「拆除模式」**，删除永远作用在已选中的东西上。
    /// </summary>
    private void DeleteSelection()
    {
        if (_hasSelection)
        {
            Submit(new RemoveAreaCommand(_selA, _selB));
            GD.Print($"[build] 拆掉框选区域 ({_selA.X},{_selA.Y})..({_selB.X},{_selB.Y})（整片一次，Ctrl+Z 一次恢复）");
            _hasSelection = false;      // 东西没了，框也收掉（免得再按一次拆到别处）
        }
        else if (_selCell is { } c)
        {
            Submit(new RemoveAtCommand(c));
        }
        else
        {
            GD.Print("[build] 没有选中任何东西：左键单击一个机器/带，或右键拖一个框出来");
        }
    }

    /// <summary>
    /// 算出「从 <paramref name="from"/> 拖到 <paramref name="to"/> 会铺出哪几段带」，
    /// 每段给两个端点。**这是预计位置与真正提交共用的唯一一份算法** ——
    /// 两边各写一遍的话，预览迟早会与实际铺出来的不一样，那种不一致比没有预览更坏。
    ///
    /// 斜着拖就自动拐一个直角（先横后竖，<paramref name="verticalFirst"/> = 先竖后横）。
    ///
    /// ⚠ **两段不能共用拐角那一格**：第一段的最后一格与第二段的第一格若是同一格，
    ///   第二段会被占用位图拒掉（「第 0 格已经有东西了」）。
    ///   所以第二段从拐角的**下一格**起步。这个坑是脚本化交互验收抓出来的。
    /// </summary>
    private static GridPos[][] PlannedBeltSegments(GridPos from, GridPos to, bool verticalFirst)
    {
        if (from.X == to.X || from.Y == to.Y) return new[] { new[] { from, to } };

        int stepX = Math.Sign(to.X - from.X);
        int stepY = Math.Sign(to.Y - from.Y);

        if (verticalFirst)
        {
            var corner = new GridPos(from.X, to.Y);
            return new[]
            {
                new[] { from, corner },
                new[] { new GridPos(corner.X + stepX, corner.Y), to },
            };
        }
        else
        {
            var corner = new GridPos(to.X, from.Y);
            return new[]
            {
                new[] { from, corner },
                new[] { new GridPos(corner.X, corner.Y + stepY), to },
            };
        }
    }

    private static bool ShiftHeld() => Input.IsPhysicalKeyPressed(Key.Shift);

    /// <summary>铺带：把拖出来的路径交给模拟侧的 BuildBeltCommand（它只接受横/竖）。</summary>
    private void SubmitBelt(GridPos from, GridPos to)
    {
        GridPos[][] segs = PlannedBeltSegments(from, to, ShiftHeld());
        // ⚠ **两段必须同层**：一段带只有一层的概念，拐角两边分属两层就自相矛盾了
        //   （而且交错判定是在"整串格子"上做的）。所以先按**全部格子的并集**挑一次层，
        //   再把同一个层号发给每一条命令 —— 与预览用的是同一个函数。
        ResolveDragLevel(segs, out int level);
        // 自动交错要在**日志里留一句**：层是看不见的参数，玩家（和排查的人）需要知道
        // 刚才那一下为什么落到了另一层。拖拽中的 HUD 提示也写着同一件事（见 UpdateBuildInteraction）。
        if (level != _beltLevel)
            GD.Print($"[build] 拖拽路径与已有轨道冲突 ⇒ 自动走{LevelName(level)}（当前选的是{LevelName(_beltLevel)}）");
        foreach (GridPos[] seg in segs)
            Submit(new BuildBeltCommand(seg[0], seg[1], level));
    }

    /// <summary>
    /// 这次拖拽最终会铺在哪一层（模拟侧 <c>TryResolveBeltLevel</c> 的同一个函数）。
    /// 返回 false = 有一段铺不了（预览会染红）。
    /// </summary>
    private bool ResolveDragLevel(GridPos[][] segs, out int level, out string? why)
    {
        var all = new List<GridPos>();
        foreach (GridPos[] seg in segs) all.AddRange(SegmentCells(seg[0], seg[1]));
        return _sim is FactorySim f
            ? f.Occupancy.TryResolveBeltLevel(all, _beltLevel, out level, out _, out why)
            : ResolveFail(out level, out why);

        static bool ResolveFail(out int lv, out string? w)
        {
            lv = 0;
            w = "没有模拟可供查询";
            return false;
        }
    }

    private bool ResolveDragLevel(GridPos[][] segs, out int level)
        => ResolveDragLevel(segs, out level, out _);

    /// <summary>这一格能不能铺带（两种层里有一层能铺就算能）—— 用于"单击 vs 拖拽起步"的判别。</summary>
    private bool CellPlaceable(GridPos cell)
        => _sim is FactorySim f &&
           f.Occupancy.TryResolveBeltLevel(new[] { cell }, _beltLevel, out _, out _, out _);

    private void SubmitPlace(GridPos cell)
    {
        BuildableDef def = _content.Buildables[_buildableIndex];
        SubmitPlace(def, OriginFor(cell, def.SizeX, def.SizeY));
    }

    private void SubmitPlace(BuildableDef def, GridPos origin)
        => Submit(new BuildMachineCommand(def, origin, _facing));

    /// <summary>多格建筑以光标为**中心**：玩家按的是「放在这儿」，而不是「以左上角对齐」。</summary>
    private static GridPos OriginFor(GridPos cell, int sizeX, int sizeY)
        => new(cell.X - (sizeX - 1) / 2, cell.Y - (sizeY - 1) / 2);

    private void HandleKey(InputEventKey key)
    {
        bool ctrl = key.CtrlPressed || key.MetaPressed;

        if (ctrl)
        {
            switch (key.Keycode)
            {
                case Key.Z when key.ShiftPressed:
                case Key.Y:
                    Submit(new RedoCommand());
                    return;
                case Key.Z:
                    Submit(new UndoCommand());
                    return;
                case Key.C:
                    if (_hasSelection)
                    {
                        Submit(new CopyAreaCommand(_selA, _selB));
                        GD.Print($"[build] 复制框选区域 ({_selA.X},{_selA.Y})..({_selB.X},{_selB.Y}) —— 结果看 HUD");
                    }
                    else
                    {
                        GD.Print("[build] 还没有框选区域：右键按住拖一个矩形出来，再 Ctrl+C");
                    }
                    return;
                case Key.V:
                    if (_sim is FactorySim f && f.ClipboardMachines + f.ClipboardBelts + f.ClipboardPipes > 0)
                    {
                        SetState(BuildState.Paste);
                        GD.Print($"[build] 进入蓝图粘贴模式（{f.ClipboardMachines} 机器 / " +
                                 $"{f.ClipboardBelts} 带 / {f.ClipboardPipes} 管道），左键落点，Esc 退出");
                    }
                    else
                    {
                        GD.Print("[build] 剪贴板是空的：右键拖一个框出来再 Ctrl+C");
                    }
                    return;
            }
            return;
        }

        // 数字键 / - / = 选可建造项
        int index = key.Keycode switch
        {
            Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4,
            Key.Key6 => 5, Key.Key7 => 6, Key.Key8 => 7, Key.Key9 => 8, Key.Key0 => 9,
            Key.Minus => 10, Key.Equal => 11,
            _ => -1,
        };
        if (index >= 0 && index < _content.Buildables.Count)
        {
            SelectBuildable(index);
            return;
        }

        switch (key.Keycode)
        {
            case Key.B: SetState(BuildState.BeltIdle); break;
            case Key.X:
            case Key.Delete:
                // 删除**永远作用在已选中的东西上**：框选就拆整片，单击选中的就拆那一个。
                // 这就是「不需要拆除模式」的实现方式 —— 没有「拆除」这个状态。
                DeleteSelection();
                break;
            case Key.R when _state == BuildState.Placing:
                // 朝向是**建造参数**，只在「正在放机器」时有意义，所以只有这个状态会转它。
                _facing = (_facing + 1) % 4;
                GD.Print($"[build] 朝向 = {_facing}（0 东 1 南 2 西 3 北）");
                break;
            case Key.T:
                // 高/低轨切换：和朝向一样是**建造参数**（见 _beltLevel 的说明）。
                _beltLevel = _beltLevel == 0 ? 1 : 0;
                GD.Print($"[build] 铺带层 = {(_beltLevel == 0 ? "低轨（贴地）" : "高轨（架在上面，会挡住低轨）")}");
                break;
            case Key.Escape:
                SetState(BuildState.Idle);
                break;
            default:
                return;
        }
    }

    /// <summary>提交一条建造命令。真正的校验在模拟线程，这里只负责发出去。</summary>
    private void Submit(BuildCommand cmd)
    {
        if (_sim is not FactorySim f) return;

        // 诊断（FORGEFLOW_CMDDIAG=1）：每条命令都打一行「谁发出来的」。
        // 排查「跑测的终态和预期不一样」时它是唯一能回答「多出来的是哪条命令、从哪进来的」的东西。
        if (_cmdDiag)
            GD.Print($"[cmd] {cmd.GetType().Name} 状态={_state} 光标={CursorCell()} " +
                     $"左={(Input.IsMouseButtonPressed(MouseButton.Left) ? 1 : 0)} " +
                     $"右={(Input.IsMouseButtonPressed(MouseButton.Right) ? 1 : 0)}");

        f.EnqueueBuild(cmd);
    }

    private bool _cmdDiag;

    /// <summary>
    /// 每帧更新悬停格与全部预览（幽灵 / 预计位置 / 拆除高亮 / 框选矩形 / 蓝图），并写 HUD。
    ///
    /// ⚠ 这里**只做预检**：真正的校验与生效在模拟线程的 ApplyBuild 里。两边都可能说「不行」，
    ///   但**只有模拟说了算**；所以预览宁可宽松也不能骗人 ——
    ///   预览说「可以」不保证一定成功，预览说「冲突」则必须是模拟也会拒的。
    ///
    /// ⚠ 这个方法必须在**快照认领之外**调用：认领只在 60Hz 的新快照上成功，
    ///   而渲染帧率上千 —— 放进认领之后就会被 `return` 跳过 90% 的帧，预览跟不上鼠标。
    /// </summary>
    private void UpdateBuildInteraction()
    {
        if (_sim is not FactorySim f) return;

        // 本帧没收到移动事件 ⇒ 说明是真实游玩（鼠标一直没动或没进这个节点），
        // 那就回退到当前光标位置（同样走 CursorCell，注入点会优先）。
        if (!_sawMotionThisFrame) _hover = CursorCell();
        _sawMotionThisFrame = false;

        OccupancyGrid grid = f.Occupancy;
        _previewUsed = 0;
        bool ghost = false;
        bool ghostOk = false;
        bool ok = false;
        string hint;

        switch (_state)
        {
            case BuildState.Idle:
            {
                // 空手：鼠标下有什么就先淡淡标一下（让玩家知道「单击会选到谁、右键单击会拆谁」）。
                bool hoverHit = TryHoverTarget(f, grid, _hover, out GridPos lo, out GridPos hi);
                if (hoverHit) AddPreviewRect(lo, hi, HoverColor);
                ok = hoverHit;
                hint = "左键单击=选中并看信息 · 右键单击=拆一个 · 右键拖=框选 · X/Delete=拆选中";
                break;
            }

            case BuildState.Placing:
            {
                BuildableDef def = _content.Buildables[_buildableIndex];
                var origin = OriginFor(_hover, def.SizeX, def.SizeY);
                ok = grid.CanPlaceMachine(origin, def.SizeX, def.SizeY);
                ghost = true;
                ghostOk = ok;
                hint = ok ? "可放" : "不可放";
                break;
            }

            case BuildState.BeltIdle:
                // ⚠ 判据是**挑层之后**能不能铺（不是"这一层空不空"）：一格上有低轨、
                //   而另一层空着时照样能起步 —— 落点会走另一层（交错）。
                ok = ResolveDragLevel(new[] { new[] { _hover, _hover } }, out int idleLevel, out string? idleWhy);
                ghost = true;
                ghostOk = ok;
                AddPreviewRect(_hover, _hover, ok ? BeltOkColor(idleLevel) : PreviewBadColor);
                hint = ok ? $"可铺（{LevelName(idleLevel)}" +
                            (idleLevel != _beltLevel ? "，自动交错·" : "，") + "按住左键拖出一整条）"
                          : $"不可铺：{idleWhy ?? "未知原因"}";
                break;

            case BuildState.BeltDrag:
            {
                // 预计位置：**真瓦片**由预览层画（见 UpdatePreviewLayer），
                // 这样拖动时就能看出「这一段是上下还是左右」、拐角拐向哪边 ——
                // 只画一个淡色矩形的话，方向要等松手才知道。
                GridPos[][] segs = PlannedBeltSegments(_dragAnchor, _hover, ShiftHeld());
                ok = ResolveDragLevel(segs, out int level, out string? why);
                hint = ok
                    ? $"预计位置（{LevelName(level)}）· 松手铺下" +
                      (level != _beltLevel ? "（自动交错）" : "") + " · T 换层 · Shift 换拐弯顺序"
                    : $"有冲突（淡红）：{why ?? "某格不可铺"} · 松手会被拒";
                break;
            }

            case BuildState.BoxSelect:
                // 框选：矩形与提示都**只在按住右键时**出现（松开就关，见 UpdateSelectionRect）。
                ok = true;
                hint = $"框选 ({_dragAnchor.X},{_dragAnchor.Y})..({_hover.X},{_hover.Y}) · " +
                       "松开只记住区域（矩形与提示同时消失）· X/Delete 才拆 · Ctrl+C 才复制";
                break;

            case BuildState.Paste:
                AddPreviewRect(_hover, _hover, PreviewOkColor);
                ok = true;
                hint = "左键落点（可连贴）· Esc 退出";
                break;

            default:
                ok = false;
                hint = "";
                break;
        }

        UpdateGhost(ghost, ghostOk);
        HideUnusedPreviewRects();

        // ⚠ 「选中的东西」不在这里画了：它现在由预览层**把物块本身重画一遍并提亮**（见 UpdatePreviewLayer）——
        //   选中状态要跟着物块走，而不是盖一个框；盖框在选中一整片时会糊成一块，看不出选中了谁。
        UpdateSelectionRect();

        UpdatePreviewLayer(f);
        UpdateSelectionInfo(f, grid);
        RefreshTrackPicker(f);

        // ── HUD ──
        if (_hudBuild != null)
        {
            // 亚像素取景：把"看不到东西"的原因写在建造行上（见 CameraController.SubPixel）。
            string subPixel = _camera?.SubPixel == true
                ? "  ⚠ 亚像素取景（一格不足 1 像素）：轨道/机器按 LOD 抽样，看不到是预期的 · 请放大" : "";
            _hudBuild.Text = $"建造 [{ToolName()}]" +
                             (_state == BuildState.Placing ? $"  朝向={_facing}" : "") +
                             (_state is BuildState.BeltIdle or BuildState.BeltDrag
                                 ? $"  层={(_beltLevel == 0 ? "低轨" : "高轨")}（T 切换）" : "") +
                             $"  光标格=({_hover.X},{_hover.Y})  {hint}{subPixel}";
        }

        if (_toolbarTitle != null)
        {
            // ⚠ 文本保持短：这一行是面板的**最小宽度**来源，太长会把整个面板撑宽、
            //   盖住厂区并吃掉那一带的鼠标点击（Label 开了 autowrap 才会按宽度折行）。
            _toolbarTitle.Text = $"建造菜单 · 撤销 {f.UndoDepth} / 重做 {f.RedoDepth}   " +
                                 $"蓝图 {f.ClipboardMachines}+{f.ClipboardBelts}+{f.ClipboardPipes}";
        }

        // ⚠ 必须判空：SetupHud 在场景缺节点、或 FORGEFLOW_HUD=0 时会**故意**把 _hud 置空并正常返回
        //   （它的注释写着「宁可没有 HUD，也不要静默的 NullReference」），而 UpdateBuildInteraction
        //   只要求 `_sim is FactorySim`，所以缺 HUD 时它照样每帧被调用 ——
        //   这里不判空 = 每帧一次 NullReferenceException（上面两个兄弟分支都是判过的）。
        if (_hudHint != null)
        {
            _hudHint.Text = $"已建 {f.MachineCount} 机器 / {f.BeltCount} 带 / {f.PipeCount} 管道    " +
                            $"接受 {f.AppliedBuildCount} 拒绝 {f.RejectedBuildCount}    " +
                            $"最近：{f.LastBuildResult.Outcome} {f.LastBuildResult.Detail}";
        }
    }

    /// <summary>
    /// 「选中的东西是什么」—— 单击选中的那一格写成一行信息。
    ///
    /// 这一段刻意**只读**模拟侧的公开访问器（<see cref="FactorySim"/> 的那批 `*Of` / `*At`），
    /// 不碰任何私有状态：HUD 是「观察者」，它要什么就得先有人把它暴露出来（那正是那些访问器存在的理由）。
    /// 名字靠 <see cref="BuildableDef.Art"/> 的图集格反查内容表 —— 所以内容表仍然是唯一真相来源。
    /// </summary>
    private void UpdateSelectionInfo(FactorySim f, OccupancyGrid grid)
    {
        if (_hudInfo == null) return;

        if (_hasSelection)
        {
            int x0 = Math.Min(_selA.X, _selB.X), x1 = Math.Max(_selA.X, _selB.X);
            int y0 = Math.Min(_selA.Y, _selB.Y), y1 = Math.Max(_selA.Y, _selB.Y);
            int m = 0, b = 0, p = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = new GridPos(x, y);
                    if (grid.MachineAt(c) >= 0) m++;
                    else if (grid.BeltAt(c) >= 0) b++;
                    else if (grid.PipeAt(c) >= 0) p++;
                }
            // ⚠ 这是一行**状态**（松手之后还剩什么），不是提示 ——
            //   所以只说事实，不写「按 X 拆 / Ctrl+C 复制」（那属于按住时的提示，见 UpdateSelectionRect）。
            _hudInfo.Text = $"框选已记住 {x1 - x0 + 1}×{y1 - y0 + 1}（{x0},{y0}）..（{x1},{y1}）：" +
                            $"机器 {m} 格 / 带 {b} 格 / 管道 {p} 格";
            return;
        }

        if (_selCell is not { } cell)
        {
            _hudInfo.Text = "选中：无（左键单击机器 / 带 / 管道看它的信息；右键拖框选）";
            return;
        }

        int mi = grid.MachineAt(cell);
        if (mi >= 0 && f.Layout.MachineAt(mi) is { } mdef)
        {
            string name = BuildableName(mdef.SpriteCell);
            string recipe = "";
            if (f.RecipeOfMachine(mi) is { } rc)
            {
                var ins = new List<string>();
                foreach (int id in rc.Inputs) ins.Add(f.Content.ItemName(id));
                var outs = new List<string>();
                foreach (int id in rc.Outputs) outs.Add(f.Content.ItemName(id));
                recipe = $" · 配方 {rc.Name}（{string.Join("+", ins)}→{string.Join("+", outs)}）";
            }
            string inv = InventorySummary(f, mi);
            string carried = f.CarriedCount(mi) > 0
                ? $"手里 {f.Content.ItemName(f.CarriedTypeOf(mi))}"
                : "手里 空";
            string face = f.FacingOf(mi) switch { 0 => "东", 1 => "南", 2 => "西", _ => "北" };
            // 接线状态要写全：**这台机器需不需要轨道角色**（种类决定）+ 各角色绑了哪几条 +
            // 还缺什么。机器必须接线才能用 —— 缺了必须一眼看出来。
            string inBind = mdef.InBeltBindings.Length > 0 ? $"（绑定 {BoundText(mdef.InBeltBindings)}）" : "";
            string outBind = mdef.OutBeltBindings.Length > 0 ? $"（绑定 {BoundText(mdef.OutBeltBindings)}）" : "";
            string wire = mdef.IsWired ? "" : $" · ⚠ 未接线（{NeedText(mdef)}）：机器不会工作";
            _hudInfo.Text = $"选中 机器 #{mi} {name} {mdef.SizeX}×{mdef.SizeY} @({mdef.Pos.X},{mdef.Pos.Y})" +
                            $" · 状态 {f.StateOf(mi)}{recipe} · {carried}{inv}" +
                            $" · 输入带 {f.InBeltCount(mi)}{inBind} / 输出带 {f.OutBeltCount(mi)}{outBind}" +
                            wire +
                            $" · 朝向 {face} · 完工 {f.RecipeCraftsFinished(mi)}" +
                            (f.PowerEnabled ? $" · 电 {f.SatOfNetwork(mdef.NetworkId) / 10}%" : "");
            return;
        }

        int bi = grid.BeltAt(cell);
        if (bi >= 0 && f.Layout.BeltAt(bi) is { } bdef)
        {
            GridPos a = bdef.Cells[0], b = bdef.Cells[^1];
            string dir = a.Y == b.Y ? (b.X > a.X ? "向东" : "向西") : (b.Y > a.Y ? "向南" : "向北");
            string up = bdef.FromMachine >= 0 ? $"机器#{bdef.FromMachine}"
                        : bdef.FromBelt >= 0 ? $"带#{bdef.FromBelt}" : "外部投料";
            string down = bdef.ToMachine >= 0 ? $"机器#{bdef.ToMachine}"
                          : bdef.ToBelt >= 0 ? $"带#{bdef.ToBelt}" : "虚空（出货口）";
            // 层要写出来：交错时"点到的这一格上面/下面还有没有带"是玩家最想知道的事。
            string stacked = grid.BeltAt(cell, 0) >= 0 && grid.BeltAt(cell, 1) >= 0
                ? $" · ⚠ 这一格上下两层都有带（低轨 #{grid.BeltAt(cell, 0)} / 高轨 #{grid.BeltAt(cell, 1)}，画的是高轨）"
                : "";
            _hudInfo.Text = $"选中 带 #{bi}（{LevelName(bdef.Level)}）{bdef.Cells.Length} 格 " +
                            $"({a.X},{a.Y})→({b.X},{b.Y}) {dir}" +
                            $" · 带上 {f.BeltItems(bi)} 件{ItemCountsOnBelt(f, bi)}" +
                            $" · 上游 {up} · 下游 {down}" +
                            $" · 累计收 {f.BeltInsertedTotal(bi)} / 送 {f.BeltRemovedTotal(bi)}{stacked}";
            return;
        }

        int pi = grid.PipeAt(cell);
        if (pi >= 0 && f.Layout.PipeAt(pi) is { } pdef)
        {
            int net = f.Layout.FluidNetworkAt(cell);
            int cells = net >= 0 ? f.Layout.FluidCellsOf(net) : 0;
            _hudInfo.Text = $"选中 管道 #{pi} {pdef.Cells.Length} 格 @({cell.X},{cell.Y})" +
                            $" · 管网 {net} ({cells} 格)" +
                            $" · 存量 {f.FluidVolume(net)}/{cells * FactorySim.FluidPerCell}" +
                            (net >= 0 && f.FluidNetworkCount > 1 ? $" · 另一侧管网 {f.FluidNetworkBOf(pi)}" : "");
            return;
        }

        _hudInfo.Text = $"选中 空格 ({cell.X},{cell.Y}) —— 这一格上没有东西";
    }

    /// <summary>机器缓冲里各有多少件（按物品名汇总，只列非空的）。</summary>
    private static string InventorySummary(FactorySim f, int m)
    {
        if (f.InvCount(m) == 0) return " · 缓冲 空";
        var counts = new Dictionary<int, int>();
        for (int k = 0; k < FactorySim.InvSlots; k++)
        {
            int t = f.InvTypeAt(m, k);
            if (t == 0) continue;
            counts[t] = counts.TryGetValue(t, out int n) ? n + 1 : 1;
        }
        var parts = new List<string>();
        foreach ((int t, int n) in counts) parts.Add($"{f.Content.ItemName(t)}×{n}");
        return " · 缓冲 " + string.Join(" ", parts);
    }

    /// <summary>带上物品的种类汇总（只列前几种，够看就行）。</summary>
    private static string ItemCountsOnBelt(FactorySim f, int b)
    {
        int n = f.BeltItems(b);
        if (n == 0) return "";
        var counts = new Dictionary<int, int>();
        for (int k = 0; k < n; k++)
        {
            int t = f.BeltTypeAt(b, k);
            if (t == 0) continue;
            counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
        }
        var parts = new List<string>();
        foreach ((int t, int c) in counts) parts.Add($"{f.Content.ItemName(t)}×{c}");
        return "（" + string.Join(" ", parts) + "）";
    }

    /// <summary>按图集格反查可建造项的名字（内容表是唯一真相来源，机器定义里不存名字）。</summary>
    private string BuildableName(int spriteCell)
    {
        foreach (BuildableDef b in _content.Buildables)
            if (b.Art.Cell == spriteCell) return b.Name;
        return "未知机器";
    }

    /// <summary>层号 → 给玩家看的名字（低轨 / 高轨 / 第 N 层）。**层名只有这一处定义**。</summary>
    private static string LevelName(int level)
        => level <= 0 ? "低轨" : level == 1 ? "高轨" : $"第 {level} 层";

    /// <summary>
    /// 幽灵（那把「按下去会长出什么」的半透明图）。**只有放置类状态才显示** ——
    /// 拆除与粘贴各有自己的预览，贴着鼠标放一个建造幽灵只会误导。
    /// </summary>
    private void UpdateGhost(bool visible, bool ok)
    {
        if (_ghost == null) return;
        _ghost.Visible = visible;
        if (!visible) return;

        _ghost.Position = new Vector2(_hover.X * FactorySim.CellSizePx + FactorySim.CellSizePx * 0.5f,
                                      _hover.Y * FactorySim.CellSizePx + FactorySim.CellSizePx * 0.5f);

        if (_state == BuildState.Placing)
        {
            MachineArt art = _content.Buildables[_buildableIndex].Art;
            (float gx, float gy, float gw, float gh) = art.FrameRect(0);
            _ghost.RegionRect = new Rect2(gx, gy, gw, gh);
            // 幽灵要与放下去之后**一样大**：内容最长边缩到一格（再乘填充系数）。
            // 与渲染路径同一条规则 —— 幽灵和实物尺寸不一致会让人以为放不下。
            float longest = MathF.Max(gw, gh);
            _ghost.Scale = Vector2.One * (FactorySim.CellSizePx * FactorySim.MachineFillRatio / longest);
            // 朝向：与 MultiMesh 路径同一套语义（绕实例中心顺时针 θ）。
            // ⚠ 角度算对了也可能看起来差 180° —— 根因在着色器的 V 轴（见 Shaders/atlas_multimesh.gdshader），
            //   不是这里的角度算错：实物被上下镜像后，左右对称的机器看起来就像少转了 180°。
            _ghost.Rotation = FactorySim.FacingRotation(_facing);
        }
        else
        {
            _ghost.RegionRect = new Rect2(FactorySim.BeltAtlasX, FactorySim.BeltAtlasY, 32, 32);
            _ghost.Scale = Vector2.One;
            _ghost.Rotation = 0f;
        }

        _ghost.Modulate = ok ? new Color(0.6f, 1f, 0.7f, 0.75f) : new Color(1f, 0.45f, 0.4f, 0.55f);
    }

    /// <summary>
    /// 框选矩形：**只在按住右键拖拽时显示**（松开就把提示关掉）。
    ///
    /// ⚠ 「提示」与「选中」是两件事，别一起关掉：
    ///   · **提示**（世界里的蓝框 + 「松开只选中 / X 才拆」那句）只属于**按住中**这个动作；
    ///   · **选中**（`_selA/_selB`）在松开后仍然有效 —— 否则「松开再按 X 拆整片」这条就没法用了。
    /// 松开后想知道框住的是什么，看 HUD 的「选中」行（那是一行**状态**，不是提示）。
    /// </summary>
    private void UpdateSelectionRect()
    {
        if (_selection == null) return;
        bool show = _state == BuildState.BoxSelect;
        _selection.Visible = show;
        if (!show) return;

        GridPos a = _dragAnchor;
        GridPos b = _hover;
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        _selection.Position = new Vector2(x0 * FactorySim.CellSizePx, y0 * FactorySim.CellSizePx);
        _selection.Size = new Vector2((x1 - x0 + 1) * FactorySim.CellSizePx,
                                      (y1 - y0 + 1) * FactorySim.CellSizePx);
    }

    /// <summary>把一块占地（两端的格坐标，含两端）写进预览矩形池。</summary>
    private void AddPreviewRect(GridPos a, GridPos b, Color color)
    {
        if (_previewUsed >= _previewRects.Length) return;
        ColorRect? r = _previewRects[_previewUsed++];
        if (r == null) return;

        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        r.Position = new Vector2(x0 * FactorySim.CellSizePx, y0 * FactorySim.CellSizePx);
        r.Size = new Vector2((x1 - x0 + 1) * FactorySim.CellSizePx,
                             (y1 - y0 + 1) * FactorySim.CellSizePx);
        r.Color = color;
        r.Visible = true;
    }

    /// <summary>这一帧没用到的矩形收起来（池是复用的，不能留下上一帧的高亮）。</summary>
    private void HideUnusedPreviewRects()
    {
        for (int i = _previewUsed; i < _previewRects.Length; i++)
            if (_previewRects[i] != null) _previewRects[i]!.Visible = false;
    }

    /// <summary>
    /// 鼠标下这一格要拆的是谁：给出它**整个占地**（机器按尺寸、带/管道按整段）。
    /// 拆一格带其实是**拆掉整段带**（见 FactorySim.RemoveAt），只高亮一格会骗人。
    ///
    /// ⚠ 读的是模拟线程的布局对象 —— 与既有的蓝图预览（ClipboardBeltCells）同一类读法。
    ///   布局在结构变更时整体重建/增删，所以这里对「刚好在这一刻被删掉」留了兜底：
    ///   拿不到对象就退化成只高亮光标那一格，不做非空断言。
    /// </summary>
    private static bool TryHoverTarget(FactorySim f, OccupancyGrid grid, GridPos cell,
                                       out GridPos lo, out GridPos hi)
    {
        lo = hi = cell;

        int m = grid.MachineAt(cell);
        if (m >= 0 && f.Layout.MachineAt(m) is { } machine)
        {
            lo = machine.Pos;
            hi = new GridPos(machine.Pos.X + machine.SizeX - 1, machine.Pos.Y + machine.SizeY - 1);
            return true;
        }

        int b = grid.BeltAt(cell);
        if (b >= 0 && f.Layout.BeltAt(b) is { } belt) return BoundsOf(belt.Cells, out lo, out hi);

        int p = grid.PipeAt(cell);
        if (p >= 0 && f.Layout.PipeAt(p) is { } pipe) return BoundsOf(pipe.Cells, out lo, out hi);

        return grid.IsOccupied(cell);   // 只拿到 id 却读不到对象：至少把这一格标出来
    }

    /// <summary>一串格子的包围盒（带与管道都是横/竖直线，所以包围盒就是它本身）。</summary>
    private static bool BoundsOf(GridPos[] cells, out GridPos lo, out GridPos hi)
    {
        lo = hi = cells.Length > 0 ? cells[0] : default;
        foreach (GridPos c in cells)
        {
            lo = new GridPos(Math.Min(lo.X, c.X), Math.Min(lo.Y, c.Y));
            hi = new GridPos(Math.Max(hi.X, c.X), Math.Max(hi.Y, c.Y));
        }
        return cells.Length > 0;
    }

    /// <summary>
    /// **预览层这一帧画什么**：蓝图粘贴 / **铺带的预计位置**（用真的带瓦片画，不是色块）。
    ///
    /// 为什么铺带预计位置要画真瓦片：玩家在拖的时候就要看出「这一段是上下还是左右」——
    /// 只画淡色矩形的话，方向只能等松手见分晓。瓦片与朝向都走
    /// <see cref="FactorySim.BeltTileAt"/>（与实际铺出来的是同一份算法，所以预览里看到的拐角就是
    /// 松手后的拐角）。颜色走预览层材质上的 `tint`（可铺淡蓝 / 冲突淡红）——
    /// ⚠ 不能用 Modulate：着色器里 `COLOR = texture(...)` 会把它覆盖掉。
    /// </summary>
    private void UpdatePreviewLayer(FactorySim f)
    {
        if (_previewBridge == null || _previewSnapshot == null) return;
        MultiMeshInstance2D? node = GetNodeOrNull<MultiMeshInstance2D>("ProbeMultiMesh");
        if (node == null) return;

        RenderSnapshot s = _previewSnapshot;
        int live = 0;
        int cap = s.Capacity;
        Color tint = Colors.White;

        bool pasting = _state == BuildState.Paste
                       && f.ClipboardMachines + f.ClipboardBelts + f.ClipboardPipes > 0;

        if (pasting)
        {
            // 带先画（压在机器下面，和正式渲染同一套分层顺序）。
            // 每一格都按**路径**取瓦片与朝向 —— 粘贴出来的拐角与原件一样。
            // ⚠ 按**层**从小到大画（低轨先、高轨后）：交错的两条带共用一格时，
            //   顺序反了预览里看到的就是"低轨压着高轨"，与粘贴结果不一致。
            for (int level = 0; level < FactoryLayout.MaxLevels; level++)
            {
                for (int bi = 0; bi < f.ClipboardBelts; bi++)
                {
                    if (f.ClipboardBeltLevel(bi) != level) continue;
                    GridPos[] cells = f.ClipboardBeltCells(bi);
                    for (int k = 0; k < cells.Length && live < cap; k++)
                    {
                        GridPos rel = cells[k];
                        var p = new GridPos(_hover.X + rel.X, _hover.Y + rel.Y);
                        (float wx, float wy) = FactoryLayout.GridToWorld(p);
                        (ushort sprite, float rot) = FactorySim.BeltTileAt(cells, k);
                        s.Xy[live * 2] = wx;
                        s.Xy[live * 2 + 1] = wy;
                        s.SpriteId[live] = sprite;
                        s.Tint[live] = 0xFFFFFFFF;
                        s.Scale[live] = 1f;
                        s.Scroll[live] = -1f;
                        s.Rot[live] = rot;
                        live++;
                    }
                }
            }

            foreach ((GridPos rel, MachineDef def) in f.ClipboardMachineDefs())
            {
                if (live >= cap) break;
                var p = new GridPos(_hover.X + rel.X, _hover.Y + rel.Y);
                // 居中 / 缩放 / 朝向全部走模拟侧的共用函数 ——
                // 预览各写一遍的话，多格建筑在这里会与放下去之后**差半格**。
                (float cx, float cy) = FactorySim.MachineCenterAt(p.X, p.Y, def);
                s.Xy[live * 2] = cx;
                s.Xy[live * 2 + 1] = cy;
                s.SpriteId[live] = FactorySim.MachineFrameCell(def.SpriteCell, def.FrameStride, 0);
                s.Tint[live] = 0xFFFFFFFF;
                s.Scale[live] = FactorySim.MachineScale(def);
                s.Scroll[live] = -1f;
                s.Rot[live] = FactorySim.FacingRotation(def);
                live++;
            }
        }
        else if (_state == BuildState.BeltDrag)
        {
            // 铺带预计位置：把「松手会铺出来的那几段」按真瓦片画出来。
            // ⚠ 颜色里带着**最终会用的那一层**（低轨淡蓝 / 高轨淡紫）：层是看不见的建造参数，
            //   颜色是唯一能在松手之前把它显示出来的东西（复用预览层的 tint uniform）。
            //   层由**同一个函数**挑（`TryResolveBeltLevel`）⇒ 预览与松手后的结果不会对不上。
            GridPos[][] segs = PlannedBeltSegments(_dragAnchor, _hover, ShiftHeld());
            bool ok = ResolveDragLevel(segs, out int level);
            tint = ok ? BeltOkColor(level) : PreviewBadColor;

            foreach (GridPos[] seg in segs)
            {
                GridPos[] cells = SegmentCells(seg[0], seg[1]);
                for (int k = 0; k < cells.Length && live < cap; k++)
                {
                    (float wx, float wy) = FactoryLayout.GridToWorld(cells[k]);
                    (ushort sprite, float rot) = FactorySim.BeltTileAt(cells, k);
                    s.Xy[live * 2] = wx;
                    s.Xy[live * 2 + 1] = wy;
                    s.SpriteId[live] = sprite;
                    s.Tint[live] = 0xFFFFFFFF;
                    s.Scale[live] = 1f;
                    s.Scroll[live] = -1f;
                    s.Rot[live] = rot;
                    live++;
                }
            }
        }
        else
        {
            // 既没在粘贴、也没在铺带 ⇒ 画**选中态**：把选中的物块自己重画一遍并提亮。
            // 这一支对「单击选中一个」与「框选一片」是同一条路 —— 区别只是选的格子范围。
            live = AppendSelectionHighlight(f, s, live);

            // 轨道选择器里选中的那条轨道也**用同一套高亮**画一遍 —— 这就是"该功能复用"：
            // 玩家在列表里点一条，世界里那一段立刻亮起来，和"单击选中一个带"是同一个视觉语言。
            if (_pickerBelt >= 0 && f.Layout.BeltAt(_pickerBelt) is { } pb)
                live = AppendBeltTiles(s, live, pb);

            if (live == 0)
            {
                node.Visible = false;
                return;
            }
            tint = SelectionTint;
        }

        // 染色：预览层材质是**自己的实例**，改它不会影响主图集层。
        if (node.Material is ShaderMaterial mat) mat.SetShaderParameter("tint", tint);

        s.LiveCount = live;
        s.Tick = 0;
        node.Visible = live > 0;
        _previewBridge.Submit(s);
    }

    /// <summary>
    /// **选中态**：把被选中的机器 / 带 / 管道按各自的精灵与朝向再画一遍（预览层会整体提亮）。
    ///
    /// 为什么按「整段带 / 整台机器」而不是「框住的格子」：这正是**按 X 会拆掉的东西** ——
    /// 拆一格带是拆整段（见 <c>FactorySim.RemoveAt</c>），所以高亮也必须整段亮，
    /// 否则玩家看到的和实际会发生的不是一回事。
    ///
    /// ⚠ 选取规则与 <see cref="FactorySim"/> 的区域拆除**一致**：机器看左上角、带/管道沾到就整段。
    /// 容量不够时截断（只是反馈；HUD 的「选中」行报的是真实数量）。
    /// </summary>
    private int AppendSelectionHighlight(FactorySim f, RenderSnapshot s, int live)
    {
        int cap = s.Capacity;

        // 选中的格子范围：框选就是那个矩形，单击选中就是那一格（多格机器靠 id 找到整台）
        GridPos a, b;
        if (_hasSelection) { a = _selA; b = _selB; }
        else if (_selCell is { } c) { a = b = c; }
        else return 0;

        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        var seenMachines = new HashSet<int>();
        var seenBelts = new HashSet<int>();
        var seenPipes = new HashSet<int>();

        for (int y = y0; y <= y1 && live < cap; y++)
            for (int x = x0; x <= x1 && live < cap; x++)
            {
                var cell = new GridPos(x, y);
                OccupancyGrid grid = f.Occupancy;

                int mi = grid.MachineAt(cell);
                if (mi >= 0 && f.Layout.MachineAt(mi) is { } mdef && seenMachines.Add(mi)
                    && mdef.Pos.X >= x0 && mdef.Pos.X <= x1 && mdef.Pos.Y >= y0 && mdef.Pos.Y <= y1)
                {
                    (float mx, float my) = mdef.WorldCenter;
                    s.Xy[live * 2] = mx;
                    s.Xy[live * 2 + 1] = my;
                    s.SpriteId[live] = FactorySim.MachineFrameCell(mdef.SpriteCell, mdef.FrameStride, 0);
                    s.Tint[live] = 0xFFFFFFFF;
                    s.Scale[live] = FactorySim.MachineScale(mdef);
                    s.Scroll[live] = -1f;
                    s.Rot[live] = FactorySim.FacingRotation(mdef);
                    live++;
                    continue;
                }

                int bi = grid.BeltAt(cell);
                if (bi >= 0 && f.Layout.BeltAt(bi) is { } bdef && seenBelts.Add(bi))
                {
                    live = AppendBeltTiles(s, live, bdef);
                    continue;
                }

                int pi = grid.PipeAt(cell);
                if (pi >= 0 && f.Layout.PipeAt(pi) is { } pdef && seenPipes.Add(pi))
                {
                    float prot = FactorySim.PipeRotation(pdef);
                    foreach (GridPos pc in pdef.Cells)
                    {
                        if (live >= cap) break;
                        (float wx, float wy) = FactoryLayout.GridToWorld(pc);
                        s.Xy[live * 2] = wx;
                        s.Xy[live * 2 + 1] = wy;
                        s.SpriteId[live] = FactorySim.PipeAtlasCell;
                        s.Tint[live] = 0xFFFFFFFF;
                        s.Scale[live] = 1f;
                        s.Scroll[live] = -1f;
                        s.Rot[live] = prot;
                        live++;
                    }
                }
            }

        return live;
    }

    /// <summary>
    /// 把**一条带**的全部瓦片追加进预览快照（按路径取瓦片与朝向）。
    /// 选中态高亮与"轨道选择器里选中的那条"共用它 —— 两处各写一份的话，
    /// 「列表里高亮的」与「点一格选中的」迟早会长得不一样。
    /// </summary>
    private static int AppendBeltTiles(RenderSnapshot s, int live, BeltDef bdef)
    {
        int cap = s.Capacity;
        for (int k = 0; k < bdef.Cells.Length && live < cap; k++)
        {
            (float wx, float wy) = FactoryLayout.GridToWorld(bdef.Cells[k]);
            (ushort sprite, float rot) = FactorySim.BeltTileAt(bdef.Cells, k);
            s.Xy[live * 2] = wx;
            s.Xy[live * 2 + 1] = wy;
            s.SpriteId[live] = sprite;
            s.Tint[live] = 0xFFFFFFFF;
            s.Scale[live] = 1f;
            s.Scroll[live] = -1f;
            s.Rot[live] = rot;
            live++;
        }
        return live;
    }

    /// <summary>一段横/竖直线（含两端）的全部格子 —— 预览与提交共用同一套端点定义。</summary>
    private static GridPos[] SegmentCells(GridPos a, GridPos b)
    {
        int stepX = Math.Sign(b.X - a.X), stepY = Math.Sign(b.Y - a.Y);
        int len = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)) + 1;
        var cells = new GridPos[len];
        for (int i = 0; i < len; i++) cells[i] = new GridPos(a.X + stepX * i, a.Y + stepY * i);
        return cells;
    }

    /// <summary>刷新 HUD。数字全部来自模拟本身，不是估算。</summary>
    private void UpdateHud()
    {
        if (_hud == null || !_hud.Visible) return;
        if (_elapsed - _hudLastUpdateSec < HudIntervalSec) return;
        _hudLastUpdateSec = _elapsed;

        long ticks = _runner?.StepCount ?? 0;
        double nowSec = Time.GetTicksMsec() / 1000.0;

        // 逻辑帧率用 ≥1 秒的窗口，否则单帧抖动会被放大成吓人的数字
        double rateDt = nowSec - _hudTickLastSec;
        if (rateDt >= 1.0)
        {
            _hudTickRate = (ticks - _hudTickLastCount) / rateDt;
            _hudTickLastCount = ticks;
            _hudTickLastSec = nowSec;
        }

        _hudStatus!.Text = $"逻辑 {_hudTickRate:F1} tick/s（目标 {SimLoopRunner.TickRate:F0}）    " +
                           $"渲染 {Engine.GetFramesPerSecond():F0} FPS    tick {ticks:N0}";
        _hudInstances!.Text = $"实例 {_lastLiveCount:N0} / {_sim?.Buffers.Capacity ?? 0:N0}    " +
                              $"interop {_bridge?.InteropCallsLastFrame ?? 0}/帧";

        if (_sim is FactorySim f)
        {
            int active = f.ActiveCount, total = f.MachineCount;
            double pct = total > 0 ? 100.0 * active / total : 0;

            if (rateDt >= 1.0)
            {
                long delta = f.ShippedTotal - _hudRateLastShipped;
                _hudRatePerMin = delta / rateDt * 60.0;
                _hudRateLastShipped = f.ShippedTotal;
            }

            _hudThroughput!.Text = $"出货 {f.ShippedTotal:N0} 件（{_hudRatePerMin:F0} 件/分）    " +
                                   $"带上物品 {f.ItemsOnBelts:N0}";
            _hudMachines!.Text = $"机器 {active}/{total} 活跃（{pct:F0}%）" +
                                 (f.PowerEnabled ? $"   电网 {f.SatOfFirstNetwork / 10}%" : "    电力 未启用");
            _hudPacing!.Text = $"带速 {f.SpeedSub} 子格/tick · 最长带 {f.CellsPerBelt} 格 · " +
                               $"机器构成 {f.MachineKindCounts()}";

            // 剔除统计：必须给出「剔除后的数字」——
            // 光有「画了多少」没用，必须同时给出**剔掉了多少**，否则看不出剔除有没有生效。
            string interop = $"{_bridge?.InteropCallsLastFrame ?? 0}/帧";
            int cap = _sim?.Buffers.Capacity ?? 0;
            if (f.CullEnabled)
            {
                int drawn = _lastLiveCount, culled = f.LastCulledInstances;
                int totalInstances = drawn + culled;
                double rate = totalInstances > 0 ? 100.0 * culled / totalInstances : 0;
                _hudInstances!.Text = $"实例 {drawn:N0} / 容量 {cap:N0}" +
                                      $"    剔除 {culled:N0}（{rate:F1}%）    interop {interop}";
            }
            else
            {
                _hudInstances!.Text = $"实例 {_lastLiveCount:N0} / {cap:N0}" +
                                      $"    剔除 关    interop {interop}";
            }
        }
        else
        {
            _hudThroughput!.Text = "出货 —（当前模拟源没有产能概念）";
            _hudMachines!.Text = "机器 —";
            _hudPacing!.Text = $"模拟源 {_sim?.GetType().Name ?? "?"}";
        }
    }

    /// <summary>
    /// 脚本化搭一条**完整产线**（验证用），顺便把 P0/P1 的成果摆到画面上：
    ///   铁矿机 → 带 → 铁熔炉 → 带 → 齿轮机 → 带 → 出货机
    ///   铜线：铜矿机 → 带 → 铜熔炉 → 带 → 电路板机（另一条输入带）
    ///   铁矿机 → 带 → 电路板机
    ///   仓储箱 + 机械臂跨一格空地 + 水塔/管道/洗矿机
    /// 全程只发建造命令 —— 和玩家点鼠标走的是同一条路。
    /// </summary>
    private void AutoBuildShowcase(FactorySim auto)
    {
        BuildableDef Def(string id) => _content.Buildable(id)!;

        // 铁线：矿机(2,3) → 带(3,3)-(8,3) → 铁熔炉(9,3) → 带(10,3)-(15,3) → 齿轮机(16,3)
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-mine"), new GridPos(2, 3)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(3, 3), new GridPos(8, 3)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-furnace"), new GridPos(9, 3)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(10, 3), new GridPos(15, 3)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("gear-assembler"), new GridPos(16, 3)));

        // 齿轮机 → 拐两个弯 → 出货机：**三段相连的带**（这段专门演示「带与带相连」）
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(17, 3), new GridPos(21, 3)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(21, 4), new GridPos(21, 6)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(22, 6), new GridPos(26, 6)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(27, 6)));

        // 铜线（竖着接到电路板机下方）：矿机(2,10) → 带(2,9)-(2,8) → 铜熔炉(2,7)
        auto.EnqueueBuild(new BuildMachineCommand(Def("copper-mine"), new GridPos(2, 10)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(2, 9), new GridPos(2, 8)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("copper-furnace"), new GridPos(2, 7)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(2, 6), new GridPos(2, 5)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("circuit-assembler"), new GridPos(2, 4)));

        // 铁板也送进电路板机：铁熔炉旁边分一条横带下来
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(9, 4), new GridPos(3, 4)));

        // 电路板机 → 带 → 出货机
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(3, 5), new GridPos(6, 5)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(7, 5)));

        // 物流件展示：仓储箱 + 机械臂跨一格空地 + 流体
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-mine"), new GridPos(2, 14)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(3, 14), new GridPos(6, 14)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("storage"), new GridPos(8, 14)));   // 与带端点隔一格 → 靠机械臂
        auto.EnqueueBuild(new BuildMachineCommand(Def("inserter"), new GridPos(7, 14)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(9, 14), new GridPos(12, 14)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(13, 14)));

        // 流体：水塔 + 管道 + 洗矿机（要水才能出铁板）
        // 管道铺成「一横一竖」相接：竖的那段**止于横段上方一格**（不能压在 (4,18) 上 ——
        // 一格只放得下一段管道，T 型靠「相邻成网」而不是靠叠格子）。这样画面上能看到
        // 横向 / 纵向两种朝向（见 FactorySim 里管道朝向由路径决定那段注释）。
        auto.EnqueueBuild(new BuildMachineCommand(Def("fluid-source"), new GridPos(2, 18)));
        auto.EnqueueBuild(new BuildPipeCommand(new GridPos(3, 18), new GridPos(4, 18)));
        auto.EnqueueBuild(new BuildPipeCommand(new GridPos(4, 16), new GridPos(4, 17)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("washer"), new GridPos(5, 18)));

        // ══════════════════════════════════════════════════════════════════════
        // **扩展展示区**：把剩下几种机器与几套经典排法都摆出来
        //（多摆几套流水线，展示机器的用法）。
        // 每一组都自成一个可读的例子，位置尽量避开已有产线。
        // ══════════════════════════════════════════════════════════════════════

        // ① **分流器**：1 进 2 出。矿机 →带→ 分流器 →带→ 两台出货机。
        //    分流器是**轮转**语义：进来的每一件**轮流**送往两条输出带，**物品类型不变**。
        //
        // ⚠ 接线铁律：**输出带的入口格必须与机器边相邻**（上下左右），不能斜着。
        //   把第二条输出带放在 (7,23) 而分流器在 (6,22) 就是**对角线**，
        //   那条带根本没接上、分流器只有一条输出（画面看起来就是「行为很怪」）。
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-mine"), new GridPos(2, 22)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(3, 22), new GridPos(5, 22)));   // 输入：西侧相邻
        auto.EnqueueBuild(new BuildMachineCommand(Def("splitter"), new GridPos(6, 22)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(7, 22), new GridPos(10, 22)));  // 输出 A：东侧相邻
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(11, 22)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(6, 21), new GridPos(6, 19)));   // 输出 B：北侧相邻
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(6, 18)));

        // ② **合流器**：2 进 1 出。两台矿机 →带→ 合流器 →带→ 出货机。
        //    合流器**按带 id 升序**取第一条有货的（确定性），所以两条线的货会交替合并。
        auto.EnqueueBuild(new BuildMachineCommand(Def("copper-mine"), new GridPos(2, 24)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(3, 24), new GridPos(4, 24)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(5, 24), new GridPos(5, 25)));   // 输入 1：西侧相邻
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-mine"), new GridPos(2, 27)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(3, 27), new GridPos(5, 27)));
        // ⚠ 两段带的格子**不能重叠**：写成 (3,27)-(6,27) 再 (6,27)-(6,26) 的话，
        //   两段都占 (6,27) ⇒ 第二段被拒（症状：核对里「拒绝=1」）。首尾相接即可。
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(6, 27), new GridPos(6, 26)));   // 输入 2：南侧相邻
        auto.EnqueueBuild(new BuildMachineCommand(Def("merger"), new GridPos(6, 25)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(7, 25), new GridPos(10, 25)));  // 输出：东侧相邻
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(11, 25)));

        // ③ **齿轮线**（2 进 1 出配方）：铁矿机 →带→ 铁熔炉 →带→ 齿轮机 →带→ 出货机。
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-mine"), new GridPos(14, 14)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(15, 14), new GridPos(18, 14)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-furnace"), new GridPos(19, 14)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(20, 14), new GridPos(23, 14)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("gear-assembler"), new GridPos(24, 14)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(25, 14), new GridPos(27, 14)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(28, 14)));

        // ④ **长臂机械臂**：跨一格空地搬运（机器与带之间留 1 格，只有机械臂够得到）。
        //    与上面那条「矿机→带→仓储箱」是同一套用法，这里换个方向再摆一次（竖着）。
        auto.EnqueueBuild(new BuildMachineCommand(Def("copper-mine"), new GridPos(14, 18)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(14, 19), new GridPos(14, 21)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("inserter"), new GridPos(14, 23)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("storage"), new GridPos(14, 24)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(15, 24), new GridPos(18, 24)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(19, 24)));

        // ⑤ **均压泵**：两段管道**不相邻**时是两张网，泵架在中间把水搬过去。
        //    （这是泵存在意义的唯一演示：没有它，右边那张网永远是空的。）
        auto.EnqueueBuild(new BuildMachineCommand(Def("fluid-source"), new GridPos(22, 18)));
        auto.EnqueueBuild(new BuildPipeCommand(new GridPos(23, 18), new GridPos(24, 18)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("pump"), new GridPos(25, 18)));
        auto.EnqueueBuild(new BuildPipeCommand(new GridPos(26, 18), new GridPos(27, 18)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("washer"), new GridPos(28, 18)));

        // ⑥ 第三条铁线（横着，与① ② 呼应）：展示「同样的机器换一种摆法照样成立」。
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-mine"), new GridPos(14, 28)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(15, 28), new GridPos(19, 28)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("iron-furnace"), new GridPos(20, 28)));
        auto.EnqueueBuild(new BuildBeltCommand(new GridPos(21, 28), new GridPos(25, 28)));
        auto.EnqueueBuild(new BuildMachineCommand(Def("shipper"), new GridPos(26, 28)));

        GD.Print("[autobuild] 已提交完整产线（铁/铜/电路 + 齿轮 + 分流/合流 + 仓储/机械臂 + 水塔/泵/洗矿）");
        _autoBuildVerifyAt = _elapsed + 1.0;
    }

    /// <summary>搭完之后隔一秒核对一次：脚本化建造也要能被验证，不能只靠肉眼看截图。</summary>
    private double _autoBuildVerifyAt = -1;

    private void VerifyAutoBuild(FactorySim f)
    {
        if (_autoBuildVerifyAt < 0 || _elapsed < _autoBuildVerifyAt) return;
        _autoBuildVerifyAt = -1;
        GD.Print($"[autobuild] 核对：机器={f.MachineCount} 带={f.BeltCount} 管道={f.PipeCount}  " +
                 $"带连接处={f.Layout.BeltLinkCount}  接受={f.AppliedBuildCount} 拒绝={f.RejectedBuildCount}  " +
                 $"最近={f.LastBuildResult.Outcome} {f.LastBuildResult.Detail}");
    }

    /// <summary>
    /// 脚本化输入序列（自动化验收用）：<c>FORGEFLOW_SIMSEQ</c>=用 <c>;</c> 分隔的步骤，
    /// 每步间隔 <see cref="SeqStepSec"/> 秒，在 <c>_elapsed ≥ 3s</c> 之后开始。
    ///
    /// 步骤写法：
    ///   <c>key:R</c>                 按一下某个键（Keycode 名字，如 R / Z / Escape / Key2）
    ///   <c>ctrl:Z</c>                按住 Ctrl 再按
    ///   <c>click:30,20</c>           在网格 (30,20) 处走一次**真实鼠标**左键按下+抬起
    ///   <c>rclick:30,20</c>          右键单击（拆掉那一格的东西）
    ///   <c>drag:30,20,40,24</c>      左键按下 → 移动到终点 → 抬起（铺带用）
    ///   <c>rdrag:30,20,40,24</c>     右键按下 → 移动到终点 → 抬起（框选，跨帧所以不会被当成单击）
    ///   <c>press:30,20</c>           左键按下并**一直按住**（要观测「拖动中」的画面时用）
    ///   <c>release</c>               松开左键（配 press 用）
    ///   <c>rpress:30,20</c> / <c>rrelease</c>  同上，但作用于**右键**（框选提示只在按住时显示）
    ///   <c>move:30,20</c>            只移动鼠标
    ///   <c>at:30,20</c>              只把「光标格」定到这一格（不产生事件）
    ///
    /// ⚠ 为什么要走真实输入管线（Input.ParseInputEvent）而不是直接调内部方法：
    ///   直接调就绕过了 _UnhandledInput、GUI 吞噬、坐标换算、按键分发这些**真正容易错的地方**，
    ///   测了等于没测。这一层之前就是「点了没反应」，正是因为鼠标事件被全屏 Control 吃掉了。
    ///
    /// ⚠ 已知差异：注入的 <c>InputEventMouseMotion</c> 里那个 <c>Position</c> 会被引擎按
    ///   stretch 变换再走一遍，所以**悬停格**在自动化跑测里不准（真实游玩没有这个问题，
    ///   因为那时用的是操作系统的真实坐标）。而**点击**用的 <c>InputEventMouseButton.Position</c>
    ///   不受影响 ⇒ 所有「落在哪一格」的判据都可靠，只是幽灵预览的位置在跑测里不可信。
    /// </summary>
    private string[] _seq = Array.Empty<string>();
    private int _seqIndex;
    private double _seqNextAt;
    private const double SeqStepSec = 0.4;

    private void ExecuteSeqStep(string step)
    {
        string[] head = step.Split(':');
        string verb = head[0].Trim().ToLowerInvariant();
        string arg = head.Length > 1 ? head[1].Trim() : "";

        // ── 轨道选择器的脚本化入口 ────────────────────────────────────────────
        //   `pick:<行号>` = 在候选列表里点第 n 行；`bind:in|out|clear` = 按对应的那个按钮。
        // ⚠ 为什么不能靠注入鼠标点面板：SIMSEQ 的坐标是**格坐标**（它定的是"光标格"，
        //   注入事件的坐标故意填诱饵 (0,0)），而 Godot 的 Button / ItemList 是按
        //   **真实鼠标位置**做命中测试的 ⇒ 注入的点击永远落不到面板上。
        //   所以这两个动作调的是**按钮与列表连的同一个处理器** —— 逻辑路径（候选表、
        //   选中态、提交命令）全都覆盖到，只有"Godot 的信号接线"这一跳是绕过的。
        if (verb == "pick" || verb == "bind")
        {
            if (verb == "pick")
            {
                if (int.TryParse(arg, out int row) && _trackList != null)
                {
                    _trackList.Select(row);
                    OnPickerRowSelected(row);
                }
                else GD.PushError($"[seq] pick 参数应为行号：'{step}'");
            }
            else
            {
                SubmitPickerBinding(arg.ToLowerInvariant() switch
                {
                    "in" => TrackRole.Input,
                    "out" => TrackRole.Output,
                    _ => TrackRole.Clear,
                });
            }
            return;
        }

        if (verb == "at" || verb == "warp" || verb == "click" || verb == "rclick" || verb == "move"
            || verb == "drag" || verb == "rdrag" || verb == "press" || verb == "rpress"
            || verb == "release" || verb == "rrelease")
        {
            // release 不需要坐标：松开就是松开，位置由注入点决定。
            // ⚠ 左右键都要能松：按住左键看「铺带预计位置」、按住右键看「框选提示」，各配各的。
            if (verb == "release" || verb == "rrelease")
            {
                MouseButton held = verb == "rrelease" ? MouseButton.Right : MouseButton.Left;
                Input.ParseInputEvent(new InputEventMouseButton
                { ButtonIndex = held, Pressed = false, Position = new Vector2(0f, 0f) });
                GD.Print($"[seq] {held} 松开");
                return;
            }

            string[] parts = arg.Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int gx) || !int.TryParse(parts[1], out int gy))
            {
                GD.PushError($"[seq] 坐标格式错：'{step}'（需要格坐标 x,y）");
                return;
            }

            // 真实光标挪移：**只用于人工诊断**（依赖窗口焦点，跑测里不可靠）。
            if (verb == "warp")
            {
                WarpCursorTo(gx, gy);          // 换算只有一处（就在 WarpCursorTo 里）
                GD.Print($"[seq] warp 真实光标到格({gx},{gy})");
                return;
            }

            // 其余动作都走**注入点**：定住「光标格」，然后注入鼠标事件触发左键。
            // 事件里的坐标故意填诱饵 (0,0)：如果哪天代码又回头去信事件坐标，
            // 落点会跑到 (0,0) 附近，用例立刻失败，不会「恰好也对」。
            _injectedCursor = new GridPos(gx, gy);
            var decoy = new Vector2(0f, 0f);

            if (verb == "at")
            {
                GD.Print($"[seq] 光标定在格({gx},{gy})");
                return;
            }

            if (verb == "move")
            {
                Input.ParseInputEvent(new InputEventMouseMotion { Position = decoy });
                GD.Print($"[seq] move 到格({gx},{gy})");
                return;
            }

            if (verb == "click" || verb == "rclick")
            {
                MouseButton btn = verb == "rclick" ? MouseButton.Right : MouseButton.Left;
                Input.ParseInputEvent(new InputEventMouseButton
                { ButtonIndex = btn, Pressed = true, Position = decoy });
                Input.ParseInputEvent(new InputEventMouseButton
                { ButtonIndex = btn, Pressed = false, Position = decoy });
                GD.Print($"[seq] {(verb == "rclick" ? "右键单击" : "click")} 格({gx},{gy})");
                return;
            }

            // press / rpress：按下并**一直按住**（后续用 release / rrelease 松开）。
            // 为什么需要它：`drag` 是「按下 + 下一帧松开」，中间没有稳定窗口，
            // 截图/观测都抓不到「正在拖」的那一刻 —— 而「拖动中的预计位置」「按住才显示的框选提示」
            // 正是要在这时看的东西。
            if (verb == "press" || verb == "rpress")
            {
                MouseButton hold = verb == "rpress" ? MouseButton.Right : MouseButton.Left;
                Input.ParseInputEvent(new InputEventMouseButton
                { ButtonIndex = hold, Pressed = true, Position = decoy });
                GD.Print($"[seq] {hold} 按住于格({gx},{gy})（用 release 松开）");
                return;
            }

            if (parts.Length < 4 || !int.TryParse(parts[2], out int ex) || !int.TryParse(parts[3], out int ey))
            {
                GD.PushError($"[seq] drag / rdrag 需要四段坐标：'{step}'");
                return;
            }
            // ⚠ drag 必须**跨两帧**：`Input.ParseInputEvent` 是**入队**的，
            //   同一帧里连续注入的「按下」与「抬起」会在下一次输入派发时一起送达 ——
            //   那时注入点已经是终点，于是按下也读成终点，拖出来是个 1 格带。
            //   （实测踩到：`带 #0（1 格）`。）所以按下这一步只发按下，终点留到下一帧。
            // ⚠ **右键拖拽也走这里** —— 框选同样必须跨帧，否则按下/松开读成同一格，
            //   会被判成「右键单击」（那会拆掉东西，而不是框选）。
            _pendingDragButton = verb == "rdrag" ? MouseButton.Right : MouseButton.Left;
            Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = _pendingDragButton, Pressed = true, Position = decoy });
            _pendingDragEnd = new GridPos(ex, ey);
            GD.Print($"[seq] {_pendingDragButton} 按下于格({gx},{gy})，终点格({ex},{ey}) 下一帧抬起");
            return;
        }

        if (verb == "key" || verb == "ctrl")
        {
            if (!Enum.TryParse(arg, ignoreCase: true, out Key kc))
            {
                GD.PushError($"[seq] 认不出按键 '{arg}'");
                return;
            }
            bool ctrl = verb == "ctrl";
            Input.ParseInputEvent(new InputEventKey { Keycode = kc, Pressed = true, CtrlPressed = ctrl });
            Input.ParseInputEvent(new InputEventKey { Keycode = kc, Pressed = false, CtrlPressed = ctrl });
            GD.Print($"[seq] {step}");
            return;
        }

        GD.PushError($"[seq] 认不出步骤 '{step}'");
    }

    private static Vector2 CellCenter(int gx, int gy) => new(
        gx * FactorySim.CellSizePx + FactorySim.CellSizePx * 0.5f,
        gy * FactorySim.CellSizePx + FactorySim.CellSizePx * 0.5f);

    /// <summary>把真实光标挪到某一格的中心（仅人工诊断用；跑测请用注入点）。</summary>
    private void WarpCursorTo(int gx, int gy) => Input.WarpMouse(WorldToViewport(CellCenter(gx, gy)));

    /// <summary>拖拽终点（跨帧：本帧只按下，下一帧才把注入点移到终点并抬起）+ 用的是哪个键。</summary>
    private GridPos? _pendingDragEnd;
    private MouseButton _pendingDragButton = MouseButton.Left;

    /// <summary>
    /// 世界坐标 ↔ 视口坐标（自动化注入坐标用）。
    ///
    /// ⚠ 必须用 <c>CanvasItem.GetViewportTransform()</c>，**不能自己拿
    ///   「相机位置 + 视口一半」手算** —— 相机那条链路上还有 offset / 锚点 / 画布变换
    ///   （实测它是对的：1280×720 下它算出的映射与相机公式一致，含 0.667 的拉伸）。
    /// </summary>
    private Vector2 WorldToViewport(Vector2 world) => GetViewportTransform() * world;

    /// <summary>
    /// 把当前可见的世界矩形推给模拟线程（剔除用）。
    ///
    /// 用 <c>GetViewportTransform()</c> 的逆把「视口的四个角」映射回世界 ——
    /// 这样相机平移、缩放、画布拉伸**全都自动算进去**，与 <see cref="WorldToViewport"/>
    /// 严格互逆（不要自己拿「相机位置 + 视口一半」手算 ——
    /// 那样注入坐标会整体偏掉）。
    ///
    /// 取四角而不是「中心 ± 半宽/半高」：后者在相机带旋转或非均匀缩放时是错的，
    /// 而取四角再求包围盒对任何仿射变换都成立。
    /// </summary>
    private void PublishViewport()
    {
        if (_sim is not FactorySim f) return;

        UpdateCullAabb(f);

        Transform2D inv = GetViewportTransform().AffineInverse();

        // ⚠⚠ **屏幕范围必须用「物理窗口像素」，不能用 `GetViewportRect().Size`。**
        //
        //   这两者**不是同一个坐标空间**，而 `GetViewportTransform()` 输出的是**窗口像素**：
        //     · `GetViewportRect().Size` = **逻辑视口**尺寸（全屏按 `aspect=expand` 会变，如 1920×1200）；
        //     · `GetViewportTransform()` 的缩放 = `camera.zoom × stretch`，`stretch = 窗口 / 逻辑视口`。
        //   ⇒ 拿逻辑视口当屏幕范围去反解，会**同时算错偏移和尺寸**。实测全屏
        //     （窗口 2560×1600 / 逻辑 1920×1200 / stretch 1.3333 / zoom 0.5）：
        //       错误矩形中心 (7984,63700)，偏左上且只有正确的 75%；
        //       正确矩形中心 (8464,64000) = 相机位置。
        //     **窗口模式下两者恰好相等 ⇒ 这个 bug 只在全屏暴露**。
        //
        //   注：这只影响「屏幕矩形 → 世界矩形」这一个方向。反向（世界点 → 屏幕点，
        //   如鼠标定位）用 `GetViewportTransform()` 仍然是对的 —— 那里给的是**点**，
        //   不存在「该用哪个尺寸」的问题。
        Vector2 size = GetWindow().Size;

        Vector2 p0 = inv * Vector2.Zero;
        Vector2 p1 = inv * new Vector2(size.X, 0f);
        Vector2 p2 = inv * new Vector2(0f, size.Y);
        Vector2 p3 = inv * size;

        float loX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float hiX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float loY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float hiY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

        // 最后一个是「世界→屏幕缩放」：Sim 侧靠它判**亚像素实例**（拉远到连一个像素都占不到
        // 的实例引擎根本画不出来，却还在上传 —— 那种情况必须直接剔掉）。
        // ⚠ 方向别搞反：`inv` 是 **screen→world**，它的 Scale 是「每屏幕像素多少世界单位」，
        //   取倒数才是我们要的 world→screen。（`GetViewportTransform().Scale` 直接就是正方向，
        //   而且它含 stretch —— 与「可见世界矩形」用的那个变换同源，口径一致。）
        f.CullViewport.Set(loX, loY, hiX, hiY, GetViewportTransform().Scale.X);
        _lastView = new Rect2(loX, loY, hiX - loX, hiY - loY);

        // 机器名 label：**只要相机动过就要重画**（理由见 MachineLabelDrawer.ShouldRedraw：
        // 拉远到阈值以下时也必须重画一次，否则最后一次画的 label 会留在屏幕上）。
        if (_labels is { } ld && ld.ShouldRedraw(GetViewportTransform().Scale.X)) QueueRedraw();
    }

    private Rect2 _lastView;

    /// <summary>机器名 label 绘制器（与压力场景共用，见 <see cref="MachineLabelDrawer"/>）。</summary>
    private MachineLabelDrawer? _labels;

    /// <summary>
    /// 画机器名 label。**两个场景走同一份实现**。
    /// ⚠ z 序：Main.tscn 里已把根节点抬到实例层之上、并把子节点设成绝对 z。
    /// 传 <c>GetViewportTransform()</c>（世界→画布，含相机与画布拉伸）—— label 靠它把
    /// 画布缩放抵消掉，字号才是稳定的**屏幕像素**（见 <see cref="MachineLabelDrawer.Draw"/>）。
    /// </summary>
    public override void _Draw()
        => _labels?.Draw(this, _lastView, GetViewportTransform());

    /// <summary>上一次同步 AABB 时的拓扑版本（去重用；稳态下每帧零成本）。</summary>
    private int _cullAabbRevision = -1;

    /// <summary>
    /// 让实例缓冲的包围盒跟着**工厂实际范围**长。
    ///
    /// ⚠ 只在构造时设一次是错的：主场景按「空地图开局」建，那一刻
    ///   <c>FactorySim.WorldBounds</c> 只有 1×1，而玩家随后会把厂子建到几万像素外。
    ///   AABB 既是「跳过 O(N) 重算」的依据，也是引擎判断该不该画这个 MultiMesh 的范围 ——
    ///   不跟着长，症状就是**整个工厂被裁掉、画面全空、且不报任何错**。
    ///
    /// 按 <see cref="FactorySim.TopologyRevision"/> 去重：只有建造/拆除过才重算。
    /// </summary>
    private void UpdateCullAabb(FactorySim f)
    {
        if (_bridge == null || f.TopologyRevision == _cullAabbRevision) return;
        _cullAabbRevision = f.TopologyRevision;

        WorldRect wb = f.WorldBounds;
        const float pad = FactorySim.CellSizePx * 2f;
        _bridge.SetCullAabb(new Aabb(
            new Vector3(wb.LoX - pad, wb.LoY - pad, -1f),
            new Vector3(wb.HiX - wb.LoX + pad * 2f, wb.HiY - wb.LoY + pad * 2f, 2f)));
    }

    /// <summary>
    /// **「光标在哪一格」的唯一来源。**
    ///
    /// ⚠ 必须用 <c>GetGlobalMousePosition()</c>，两条理由：
    ///
    ///   1. **与幽灵同源**：幽灵是 Sprite2D，由引擎按**当前画布变换**画在世界上；
    ///      `GetGlobalMousePosition()` 用的是同一套变换的逆 ⇒ 幽灵必然落在光标下、
    ///      建造也就必然落在幽灵那一格。这是「所见即所放」的唯一保证。
    ///   2. **不能拿鼠标事件的 <c>Position</c> 定位**：那条路读到的坐标**可能是滞后的**
    ///      （操作系统会把移动事件合并成批、引擎又在帧边界才派发）。实测同一次移动里
    ///      「事件坐标」与「此刻真实光标」能差 170px 并因此落到**相邻的另一格**。
    ///      症状正是「幽灵跟在光标后面、放下去的位置和鼠标不符」。
    ///
    /// 事件坐标现在只用于诊断输出（<c>FORGEFLOW_MOUSEDIAG=1</c>），不再参与定位。
    ///
    /// <see cref="_injectedCursor"/> 是给自动化用的**注入点**：真实游玩时恒为 null。
    /// （不能让自动化去挪真实光标 —— <c>Input.WarpMouse</c> 在窗口没有焦点时会被引擎直接忽略，
    ///   实测时灵时不灵，跑测不可复现。）
    /// </summary>
    private GridPos CursorCell() => _injectedCursor ?? WorldToGrid(GetGlobalMousePosition());

    /// <summary>自动化注入的光标格（测试用；真实游玩恒为 null）。</summary>
    private GridPos? _injectedCursor;

    /// <summary>跑脚本化输入，并在每步之后打印一次模拟侧的可观测结果。</summary>
    private void TickSequence()
    {
        // 先收掉上一帧「拖拽按下」的尾巴：把注入点移到终点，再注入抬起。
        // （必须跨帧，理由见 drag 分支：ParseInputEvent 是入队的。）
        if (_pendingDragEnd is { } end)
        {
            _pendingDragEnd = null;
            _injectedCursor = end;
            Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = _pendingDragButton, Pressed = false, Position = new Vector2(0f, 0f) });
            GD.Print($"[seq] 拖拽抬起于格({end.X},{end.Y})（{_pendingDragButton}）");
            _seqNextAt = _elapsed + SeqStepSec;
        }
        else if (_seqIndex < _seq.Length && _elapsed >= 3.0 && _elapsed >= _seqNextAt)
        {
            string step = _seq[_seqIndex++];
            _seqNextAt = _elapsed + SeqStepSec;
            ExecuteSeqStep(step);
        }
        else
        {
            return;
        }

        // 命令要等下一个 tick 才生效，所以这里只是「打完就报当前状态」；
        // 真正的验收看最后那行 SEQSUMMARY。
        if (_sim is FactorySim f)
        {
            // ⚠ 光标格取**注入点优先**：_hover 要等下一次输入派发才更新，
            //   刚注入完就打状态会打出一个过期的值，白白误导排查。
            int hx = _injectedCursor?.X ?? _hover.X;
            int hy = _injectedCursor?.Y ?? _hover.Y;
            GD.Print($"[seq]   状态：机器={f.MachineCount} 带={f.BeltCount} 管道={f.PipeCount} " +
                     $"带连接处={f.Layout.BeltLinkCount} 移交={f.BeltTransferCount} " +
                     $"接受={f.AppliedBuildCount} 拒绝={f.RejectedBuildCount} " +
                     $"撤销={f.UndoDepth} 重做={f.RedoDepth} " +
                     $"剪贴板={f.ClipboardMachines}+{f.ClipboardBelts}+{f.ClipboardPipes} " +
                     $"光标格=({hx},{hy}) 最近格=({f.LastBuildResult.Pos.X},{f.LastBuildResult.Pos.Y}) " +
                     $"朝向={_facing} 状态={_state}/{ToolName()} " +
                     $"预览块={_previewUsed} 幽灵={(_ghost?.Visible ?? false)} " +
                     $"最近={f.LastBuildResult.Outcome} {f.LastBuildResult.Detail}");
        }

        if (_seqIndex >= _seq.Length && _pendingDragEnd == null)
        {
            // ⚠ 序列跑完就**把注入点交还给真实光标**（`_injectedCursor = null`）。
            //   否则它会一直钉在最后一格上：之后再有什么输入（真人来点、或截图脚本的余波）
            //   会全部落到那一格，看起来就像「凭空多出几条命令」，排查时极难想到。
            // ⚠ 但**拖拽还在进行中时不能放**：那样光标瞬间跳到真实鼠标的位置，
            //   「按住不松手看预计位置」的截图就拍到一条从起点拉到屏幕外的巨型带
            //   （实测拍过一次：HUD 里的光标格变成 (-56,-11)）。
            if (_injectedCursor != null && !IsDragging(_state))
            {
                _injectedCursor = null;
                GD.Print("[seq] 序列结束，注入点已释放（之后用真实光标）");
            }
            GD.Print("[seq] SEQSUMMARY 见上面最后一条状态行");
        }
    }

    private void ApplyEnvOverrides()
    {
        string? v = Environment.GetEnvironmentVariable("FORGEFLOW_INSTANCES");
        if (!string.IsNullOrEmpty(v) && int.TryParse(v, out int n) && n > 0) InstanceCount = n;

        v = Environment.GetEnvironmentVariable("FORGEFLOW_ITEM_SIZE");
        if (!string.IsNullOrEmpty(v) && float.TryParse(v, out float sz) && sz > 0) ItemSizePx = sz;

        v = Environment.GetEnvironmentVariable("FORGEFLOW_SPEED");
        if (!string.IsNullOrEmpty(v) && float.TryParse(v, out float sp)) ItemSpeed = sp;
    }

    private void ReportFinal()
    {
        if (_bridge == null) return;
        GD.Print($"[final] 运行 {_elapsed:F1}s  最后 interop={_bridge.InteropCallsLastFrame}");
    }
}
