using Godot;
using ForgeFlow.Sim;

// =============================================================================
// RenderBridge —— 把 RenderSnapshot 写成 Godot MultiMesh 的实例缓冲。
//
// ⚠ 工程约定：**节点与资源一律在场景里预先建好**（Scenes/Main.tscn 里已有
//   MultiMeshInstance2D + MultiMesh + QuadMesh + ShaderMaterial 子资源）。
//   本类只绑定并写数据，不创建节点。
//
// 四条源码级结论决定了这里的写法（来源：Godot master
// servers/rendering/renderer_rd/storage_rd/mesh_storage.cpp 与 scene/resources/multimesh.cpp）：
//
// ① **2D 实例的 stride 是 8 个 float，不是 6**：
//       [c0.x, c1.x, 0, c2.x,  c0.y, c1.y, 0, c2.y]
//    即两个 vec4，每行第 3 个分量是填充 0。轴对齐物体化简为
//       [sx, 0, 0, px,  0, sy, 0, py]
//    绝不能把 Transform2D[] 直接 marshal 进去（godot#103260，Closed as not planned）。
//
// ② **必须显式设置 CustomAabb**。_multimesh_set_buffer 里唯一致命的开销是
//    未设 custom_aabb 时对全部实例做一次 O(N) 的 AABB 重算。
//    实测（10 万实例）：已设 2.4ms/帧，未设 4.5ms/帧 —— 这一行值约 2.1ms。
//
// ③ **VisibleInstanceCount 只截断前缀**，且不改 buffer 尺寸。
//    本项目的快照是每帧整块重建的，存活物品天然落在紧凑前缀里，因此不需要 swap-remove。
//
// ④ **图集靠 custom_data 逐实例给 UV 原点**（着色器见 Shaders/atlas_multimesh.gdshader）。
//    所以每实例是 16 float：8 变换 + 4 颜色 + 4 custom_data。
//    ⚠ 官方 FAQ 明确「C# 大量调用引擎 API 会因 marshalling 变慢」，所以每帧只有
//      一次 SetBuffer；实测关掉颜色可省约 0.9ms/帧（10 万实例），
//      是实例数继续变大时的第一个优化点（届时颜色烘进图集）。
// =============================================================================

/// <summary>把 RenderSnapshot 一次提交到 MultiMesh。只在主线程/渲染线程调用。</summary>
public sealed class RenderBridge
{
    public const int TransformFloats = 8;   // 2D 变换
    public const int ColorFloats = 4;       // RGBA
    public const int CustomFloats = 4;      // 图集 UV 原点

    private readonly MultiMeshInstance2D _instance;
    private readonly MultiMesh _multiMesh;

    /// <summary>
    /// 每帧上传的实例缓冲。**长度必须恰好等于 <c>InstanceCount × stride</c>**
    /// （<c>SetBuffer</c> 的硬要求），所以它会随 <see cref="_uploadCapacity"/> 一起换。
    /// </summary>
    private float[] _buffer;

    private readonly int _capacity;

    /// <summary>当前上传容量（= MultiMesh.InstanceCount）。可见实例变多时会自动长大，见 <see cref="EnsureUploadCapacity"/>。</summary>
    private int _uploadCapacity;

    private readonly Vector2 _itemSize;
    private Aabb _cullAabb;

    /// <summary>
    /// <c>FORGEFLOW_NO_CUSTOM_AABB=1</c> 的对照实验开关（关掉设 AABB ⇒ 引擎每帧对全部实例
    /// 做一次 O(N) 重算，实测约 2.1ms/帧）。
    ///
    /// ⚠ **必须记住这个开关**：有三处会写 <c>CustomAabb</c> —— 构造 / 改上传容量 / 结构变更。
    ///   只在其中一处尊重它的话，之后任何一次扩容或建东西都会把 AABB 又设回去，
    ///   于是「关 AABB」那组对照实际测到的是「一会儿开一会儿关」的混合值，数字没有意义。
    ///   所有写入一律走 <see cref="ApplyCullAabb"/>。
    /// </summary>
    private readonly bool _noCustomAabb;

    /// <summary>按对照开关决定要不要真的设 <c>CustomAabb</c>。<b>三处写 AABB 都必须走这里。</b></summary>
    private void ApplyCullAabb() => _multiMesh.CustomAabb = _noCustomAabb ? default : _cullAabb;
    private readonly bool _useColors;
    private readonly bool _useCustomData;

    private readonly float[] _uvLut;     // cell -> (u, v, uvScaleX, uvScaleY)
    private readonly float[] _sizeLut;   // cell -> (世界宽, 世界高)，**单位 = 格**（1.0 = 一格）
    private readonly int _lutCells;

    private int _lastVisible = -1;

    /// <summary>缩容下限 = 初始按视口估出来的容量（不缩到它以下，免得又立刻不够用）。</summary>
    private readonly int _uploadFloor;

    /// <summary>连续多少帧「可见实例远低于容量」，用于迟滞缩容。</summary>
    private int _lowFrames;

    /// <summary>
    /// 每帧 <c>SetBuffer</c> 实际上传的实例上限（= <c>MultiMesh.InstanceCount</c>）。
    /// 上传字节数 = 它 × <see cref="Stride"/> × 4。见构造函数的说明。
    /// </summary>
    public int UploadCapacity => _uploadCapacity;

    /// <summary>每帧上传的字节数（诊断/压测用）。</summary>
    public double LastUploadMb { get; private set; }

    /// <summary>每实例 float 数。</summary>
    public int Stride => TransformFloats + (_useCustomData ? CustomFloats : 0) + (_useColors ? ColorFloats : 0);

    /// <summary>
    /// 实例缓冲的**字段顺序**：变换(8) → custom_data(4) → 颜色(4)。
    ///
    /// ⚠ 这不是「变换 → 颜色 → custom_data」。后者更符合直觉，但实测会**全透明**：
    /// custom_data 的 (u, v, 0, 0) 被引擎当成颜色读 ⇒ alpha = 0 ⇒ 什么都不显示。
    /// 这个 bug 排查了很久，因为没有任何报错，SetBuffer 的尺寸校验也通过（stride 一样）。
    /// 定位手段是逐变量二分（只开颜色 / 只开 custom_data / 全开）+ 截图比对。
    /// </summary>
    private int ColorOffset => TransformFloats + (_useCustomData ? CustomFloats : 0);

    /// <summary>上一帧的引擎调用次数（探针用）。</summary>
    public int InteropCallsLastFrame { get; private set; }

    /// <summary>上一帧「纯托管填充」耗时（ms）。与 SetBuffer 分开计时，用于定位卡顿来源。</summary>
    public double LastFillMs { get; private set; }

    /// <summary>上一帧 <c>SetBuffer</c> 耗时（ms）—— 含托管→原生 marshal 与 GPU 上传，可能阻塞。</summary>
    public double LastSetBufferMs { get; private set; }

    private readonly System.Diagnostics.Stopwatch _stageClock = new();

    /// <param name="instance">场景里预先建好的 MultiMeshInstance2D（其 Multimesh 必须已配好 Mesh）。</param>
    /// <param name="itemSize">**一个格在世界里多少像素**（= <c>FactorySim.CellSizePx</c>）。</param>
    /// <param name="atlasColumns">图集列数（sierrassets automation pack 是 10×118）。</param>
    /// <param name="atlasRows">图集行数。</param>
    /// <param name="spriteLut">
    /// 逐精灵的图集映射 **(U 原点, V 原点, X 取样缩放, Y 取样缩放)**，长度 = 列数 × 行数 × 4。
    /// 由 <c>FactorySim.BuildSpriteLut()</c> 提供。传 null 则退化为「一格对一格、整格取样」。
    ///
    /// 为什么需要它：这套美术里**机器与物品的内容都不在单元格中心**、而且大小差很多
    /// （18×51 .. 47×31）。按整格画必然错位；只给一个 uniform 缩放又会把**非方形**内容的
    /// 邻格像素一起裁进来（症状：机械臂上出现邻格的图）。所以两个轴要分开给。
    /// </param>
    /// <param name="sizeLut">
    /// 逐精灵的**世界尺寸（单位 = 格）**，长度 = 列数 × 行数 × 2。
    /// 由 <c>FactorySim.BuildSpriteSizeLut()</c> 提供。传 null ⇒ 全部按一格算。
    /// 它保证「每个物块正好落在一格内」—— 尺寸只有一个真相来源，不在这边另算一遍。
    /// </param>
    public RenderBridge(MultiMeshInstance2D instance, int capacity, Vector2 itemSize, Aabb cullAabb,
                        bool useColors = true, bool useCustomData = true,
                        int atlasColumns = 10, int atlasRows = 118,
                        float[]? spriteLut = null, float[]? sizeLut = null,
                        int uploadCapacity = 0)
    {
        _instance = instance;
        _multiMesh = instance.Multimesh
            ?? throw new InvalidOperationException("MultiMeshInstance2D.Multimesh 为空 —— 场景里必须先配好 MultiMesh 资源。");
        _capacity = capacity;

        // ⚠ **上传容量**（MultiMesh.InstanceCount）与**快照容量**是两件事，必须分开。
        //
        // 为什么：`MultiMesh.SetBuffer()` 要求数组长度**恰好**等于 `InstanceCount × stride`，
        // 也就是说它上传的是**整个实例缓冲**，而 `VisibleInstanceCount` **只截断绘制、不减少上传**。
        // 实测（百万场景，stride=12）：画出 234 个实例时 SetBuffer 仍要 **28ms**
        //   —— 因为 2,008,192 × 12 float × 4B = **92 MB** 照传不误，约 3.3 GB/s，
        //      正好卡在这台机器的内存带宽上。
        // ⇒ **只做「视口剔除」而不把 InstanceCount 降下来，渲染侧的瓶颈一分钱都省不掉。**
        //
        // 所以：InstanceCount 取 min(快照容量, uploadCapacity)，默认等于快照容量
        // （主场景 65,536 ⇒ 上传量与快照容量相同，不改变任何既有数字）。
        // 大场景（百万档）显式给一个小得多的上传容量，配合剔除把上传量压回帧预算内。
        //
        // 代价：**画出的实例数不能超过上传容量**。超过的部分会被 Submit 丢弃并报警 ——
        // 这是一个显式的上限，不是静默截断。
        _uploadCapacity = uploadCapacity > 0 ? Math.Min(uploadCapacity, capacity) : capacity;
        _uploadFloor = _uploadCapacity;      // 缩容不会低于这个「按视口估的起点」

        _itemSize = itemSize;
        _cullAabb = cullAabb;
        _noCustomAabb = System.Environment.GetEnvironmentVariable("FORGEFLOW_NO_CUSTOM_AABB") == "1";
        _useColors = useColors;
        _useCustomData = useCustomData;
        _buffer = new float[_uploadCapacity * Stride];

        _lutCells = atlasColumns * atlasRows;
        _uvLut = new float[_lutCells * 4];
        _sizeLut = new float[_lutCells * 2];
        for (int c = 0; c < _lutCells; c++)
        {
            _sizeLut[c * 2] = 1f;
            _sizeLut[c * 2 + 1] = 1f;
        }

        if (spriteLut != null && spriteLut.Length == _lutCells * 4)
        {
            Array.Copy(spriteLut, _uvLut, spriteLut.Length);
        }
        else
        {
            if (spriteLut != null)
                GD.PushWarning($"spriteLut 长度 {spriteLut.Length} ≠ 期望 {_lutCells * 4}，退化为整格取样。");
            for (int c = 0; c < _lutCells; c++)
            {
                _uvLut[c * 4] = (float)(c % atlasColumns) / atlasColumns;
                _uvLut[c * 4 + 1] = (float)(c / atlasColumns) / atlasRows;
                _uvLut[c * 4 + 2] = 1f;
                _uvLut[c * 4 + 3] = 1f;
            }
        }

        if (sizeLut != null && sizeLut.Length == _lutCells * 2)
        {
            Array.Copy(sizeLut, _sizeLut, sizeLut.Length);
        }
        else if (sizeLut != null)
        {
            GD.PushWarning($"sizeLut 长度 {sizeLut.Length} ≠ 期望 {_lutCells * 2}，全部按一格算。");
        }
    }

    /// <summary>
    /// 按运行期配置对齐资源参数。**顺序有讲究**：改变 UseColors / UseCustomData / TransformFormat
    /// 都会重建实例缓冲，所以必须先设格式、再设 InstanceCount。
    /// </summary>
    public void Configure()
    {
        _multiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
        _multiMesh.UseColors = _useColors;
        _multiMesh.UseCustomData = _useCustomData;

        // 项目约定：**节点与资源一律在场景里预建**，找不到就报错，不做动态兜底。
        // **不要**在这里 new 一个 QuadMesh 顶上：它的 Size 默认是 1×1，会把**所有实例的可视尺寸**
        // 悄悄改掉 —— 症状比直接报错难查得多。同一个文件对「Multimesh 为 null」也是抛异常
        // （见构造函数），两处口径必须一致。
        if (_multiMesh.Mesh == null)
            throw new InvalidOperationException(
                "ItemMultiMesh 的 MultiMesh.Mesh 为空 —— 请在 Scenes/Main.tscn 里给它指定 QuadMesh 资源。");

        _multiMesh.InstanceCount = _uploadCapacity;

        // ② 关键：跳过 O(N) 的 AABB 重算（对照实验开关见 _noCustomAabb）。
        ApplyCullAabb();

        // 着色器没接上就说明场景配置漏了 —— 报出来，不要静默画成一堆白块
        if (_instance.Material == null)
            GD.PushWarning("MultiMeshInstance2D 上没有材质 —— 图集不会生效。" +
                           "请在 Scenes/Main.tscn 里给该节点挂上 ShaderMaterial。");

        // 排除法开关：去掉材质后若能看到纯色矩形，说明几何/变换/颜色这条链是通的，
        // 问题就只在着色器或贴图上。
        if (System.Environment.GetEnvironmentVariable("FORGEFLOW_NO_ATLAS") == "1")
            _instance.Material = null;
    }

    /// <summary>
    /// 保证上传容量装得下 <paramref name="need"/> 个实例；不够就**翻倍扩容**，
    /// 而**长期用不满时会自动缩回去**（见 <see cref="ShrinkAfterFrames"/> 的说明）。
    ///
    /// 为什么要动态调：上传容量决定每帧 <c>SetBuffer</c> 的量（它 = 一次 92MB 还是 1.8MB 的区别），
    /// 所以不能一上来就按快照容量申请；但可见实例数又随**窗口尺寸与缩放**变化，
    /// 不能只按初始视口估死。两者兼顾的办法就是「按需涨、闲时缩」。
    ///
    /// ⚠ **只涨不缩是个真踩到的坑**：实测百万场景里相机偶尔被拉到 0.2/0.1 倍（可见实例
    ///   从 8.6k 涨到 5万），峰值一过，容量就**永久**停在 618,496（28.31 MB/帧，SetBuffer 8.4ms）
    ///   —— 而画面里其实只有几千个实例。剔除省下来的钱被这一次瞬时高峰全吃回去了。
    /// </summary>
    private void EnsureUploadCapacity(int need)
    {
        // ① 涨：必须立即（否则画面会缺东西）
        if (need > _uploadCapacity)
        {
            _lowFrames = 0;
            ResizeTo(Math.Min(GrowTo(_uploadCapacity, need), _capacity), "可见实例变多");
            return;
        }

        // ② 缩：连续 <see cref="ShrinkAfterFrames"/> 帧都"远低于容量"才缩。
        //    用「连续帧数」而不是「单帧」是为了不在相机来回动时反复重建实例缓冲；
        //    用 1/4 而不是 1/2 是留出迟滞，避免在边界上抖动。
        if (_uploadCapacity > _uploadFloor && need * 4 < _uploadCapacity)
        {
            if (++_lowFrames < ShrinkAfterFrames) return;
            _lowFrames = 0;
            int target = Math.Max(_uploadFloor, NextPowerOfTwo(need));
            ResizeTo(target, "长期用不满");
            return;
        }

        _lowFrames = 0;
    }

    /// <summary>
    /// 连续多少帧「可见实例不足容量的 1/4」才缩容。
    ///
    /// ⚠ 这个迟滞**必须够长**：改 <c>InstanceCount</c> 会让引擎**重建整个实例缓冲**
    ///   （GPU 侧重新分配），实测一次重建就能让 <c>SetBuffer</c> 冲到 **40ms 量级**。
    ///   迟滞只有 90 帧（1.5 秒）时，相机被连续滚动就会来回扩缩 ⇒ 尖峰不断
    ///   （实测那一轮 `SetBuffer` max = **43.8ms**）。
    ///   3 秒（本值）时，稳态与「偶尔动一下相机」都完全不重建：
    ///   干净测量下 `SetBuffer` avg **0.564ms / max 1.104ms**。
    ///
    ///   ⇒ **结论：剔除去掉的是「上传量」，而尖峰来自「重建」** —— 两者是两件事，
    ///     所以要的不是「双 MultiMesh 乒乓」（那是治上传量的），而是**别频繁改 InstanceCount**。
    /// </summary>
    private const int ShrinkAfterFrames = 180;     // 约 3 秒（60 帧/秒）

    private static int GrowTo(int from, int need)
    {
        int v = Math.Max(from, 1);
        while (v < need) v *= 2;
        return v;
    }

    private static int NextPowerOfTwo(int v)
    {
        int p = 1;
        while (p < v) p *= 2;
        return p;
    }

    /// <summary>换到新的上传容量：重建实例缓冲，并**补回 CustomAabb**（重建会清掉它）。</summary>
    private void ResizeTo(int capacity, string reason)
    {
        if (capacity == _uploadCapacity) return;
        _uploadCapacity = capacity;
        _buffer = new float[capacity * Stride];
        _multiMesh.InstanceCount = capacity;
        // 不补回来 ⇒ 每帧 O(N) 重算 AABB，白丢几 ms（对照开关见 ApplyCullAabb）
        ApplyCullAabb();
        _lastVisible = -1;                     // VisibleInstanceCount 需要重新设

        if (System.Environment.GetEnvironmentVariable("FORGEFLOW_RENDERDIAG") == "1")
            GD.Print($"[render] 上传容量 {reason} ⇒ {capacity:N0} 实例" +
                     $"（{capacity * (long)Stride * 4 / 1024.0 / 1024.0:F2} MB/帧）");
    }

    /// <summary>
    /// 更新实例缓冲的包围盒（<c>CustomAabb</c>）。
    ///
    /// ⚠ **必须随工厂长大而更新**：主场景开局是空地图，`FactorySim.WorldBounds` 在那一刻
    ///   只有 1×1（退化矩形），而玩家随后会把厂子建到几万像素外。
    ///   只在构造时设一次的话，那个 AABB 就**永远停在空地图上** ——
    ///   它既是「跳过 O(N) 重算」的依据，也是引擎判断该不该画这个 MultiMesh 的范围，
    ///   一旦两者不一致，症状就是**整个工厂被裁掉、画面全空、且不报任何错**。
    ///
    /// 调用方按 `TopologyRevision` 去重，所以稳态下不会每帧调用。
    /// </summary>
    public void SetCullAabb(Aabb aabb)
    {
        _cullAabb = aabb;
        ApplyCullAabb();
    }

    /// <summary>提交一帧快照。稳态每帧 1 次引擎调用。</summary>
    public void Submit(RenderSnapshot s)
    {
        int n = s.LiveCount;
        if (n > _capacity) n = _capacity;

        // ⚠ 写出的实例不能超过**上传容量**（见构造函数的说明：InstanceCount 决定上传量）。
        //
        //   ⚠ **不要**在这里「超出就丢弃 + 警告」：**可见实例变多是完全正常的**
        //   —— 把窗口拉大、全屏、或把相机拉远，可见格数会成倍增长，
        //   而上传容量是按初始视口算的。丢弃的症状是「画面缺东西」，看起来只像「东西少了一点」，
        //   非常难发现。所以必须**按需长大**：容量不够就翻倍扩容。
        //
        //   扩容只发生在「可见实例比上次多」时（成倍增长 ⇒ 摊还 O(1) 次重建），
        //   稳态下每帧零次分配。相机不动时完全不会触发。
        EnsureUploadCapacity(n);
        if (n > _uploadCapacity) n = _uploadCapacity;

        _stageClock.Restart();

        float sx = _itemSize.X;
        float sy = _itemSize.Y;
        float[] buf = _buffer;
        float[] xy = s.Xy;
        float[] scale = s.Scale;
        float[] scroll = s.Scroll;
        float[] axis = s.ScrollAxis;
        float[] rot = s.Rot;
        ushort[] sprite = s.SpriteId;
        uint[] tint = s.Tint;
        int stride = Stride;

        // 布局：变换(8) | custom_data(4，可选) | 颜色(4，可选)  ← 顺序见 ColorOffset 的说明
        int colorOffset = _useColors ? ColorOffset : -1;
        int customOffset = TransformFloats;

        for (int i = 0; i < n; i++)
        {
            int p = i * stride;
            int q = i * 2;
            float sc = scale[i];

            int cell = sprite[i];
            if ((uint)cell >= (uint)_lutCells) cell %= _lutCells;

            // 实例尺寸 = 世界单位 × **该精灵自己的尺寸（单位=格）** × 多格倍数。
            // 尺寸来自逐精灵尺寸表 ⇒「每个物块正好落在一格内」，网格线才读得出来。
            float bx = sx * _sizeLut[cell * 2] * sc;
            float by = sy * _sizeLut[cell * 2 + 1] * sc;

            // 8 float：2D 变换 [c0.x, c1.x, 0, c2.x, c0.y, c1.y, 0, c2.y]
            //
            // 无旋转时退化成轴对齐 [bx, 0, 0, px, 0, by, 0, py]。
            // 有旋转时按 (cosθ, sinθ) 展开：c0 = (cosθ*bx, sinθ*bx)、c1 = (-sinθ*by, cosθ*by)。
            // 机器朝向走的就是这条 —— 绕实例中心转，所以多格建筑旋转后仍占同一片地。
            float theta = rot[i];
            if (theta == 0f)
            {
                buf[p] = bx; buf[p + 1] = 0f; buf[p + 2] = 0f; buf[p + 3] = xy[q];
                buf[p + 4] = 0f; buf[p + 5] = by; buf[p + 6] = 0f; buf[p + 7] = xy[q + 1];
            }
            else
            {
                float c = MathF.Cos(theta), sn = MathF.Sin(theta);
                buf[p] = c * bx; buf[p + 1] = -sn * by; buf[p + 2] = 0f; buf[p + 3] = xy[q];
                buf[p + 4] = sn * bx; buf[p + 5] = c * by; buf[p + 6] = 0f; buf[p + 7] = xy[q + 1];
            }

            if (colorOffset >= 0)
            {
                uint c = tint[i];
                int o = p + colorOffset;
                buf[o] = ((c >> 16) & 0xFF) / 255f;
                buf[o + 1] = ((c >> 8) & 0xFF) / 255f;
                buf[o + 2] = (c & 0xFF) / 255f;
                buf[o + 3] = ((c >> 24) & 0xFF) / 255f;
            }

            if (_useCustomData)
            {
                int o = p + customOffset;
                float scrollPhase = scroll[i];
                buf[o] = _uvLut[cell * 4];            // U 原点（内容框左上角）
                buf[o + 1] = _uvLut[cell * 4 + 1];    // V 原点
                // z：滚动相位（≥0 = 要滚动）；**负数 = 不滚动，此时 |z| 是 Y 向取样缩放**。
                //    这一位兼职两件事的原因见 Shaders/atlas_multimesh.gdshader 的注释：
                //    custom_data 只有 4 个 float，而静态精灵根本用不到滚动相位，
                //    却需要两个轴各自的取样缩放（非方形内容框的唯一办法）。
                // ⚠ 分界线是 **0**，不是 −0.5：物品图标的高度只有 11~14px ⇒ |z| ≈ 0.34~0.44，
                //   用 −0.5 当界会把它们误判成带面（取样缩放变成整格），图标于是又小又偏。
                bool belt = scrollPhase >= 0f;
                buf[o + 2] = belt ? scrollPhase : -_uvLut[cell * 4 + 3];
                // w：**带面时是流向轴**（+1 沿 X / -1 沿 Y，带面恒为整格取样，所以这一位空着）；
                //    静态精灵时是 X 向取样缩放。少了它会拿横向相位去平移纵向带（见 RenderSnapshot.ScrollAxis）。
                buf[o + 3] = belt ? axis[i] : _uvLut[cell * 4 + 2];
            }
        }

        int interop = 0;

        if (n != _lastVisible)
        {
            _multiMesh.VisibleInstanceCount = n;
            _lastVisible = n;
            interop++;
        }

        _stageClock.Stop();
        LastFillMs = _stageClock.Elapsed.TotalMilliseconds;

        _stageClock.Restart();
        _multiMesh.SetBuffer(_buffer);
        _stageClock.Stop();
        LastSetBufferMs = _stageClock.Elapsed.TotalMilliseconds;
        LastUploadMb = _uploadCapacity * (double)stride * 4 / 1024.0 / 1024.0;
        interop++;

        InteropCallsLastFrame = interop;
    }
}
