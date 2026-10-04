namespace ForgeFlow.Sim;

/// <summary>
/// 占位模拟：物品沿 +X 匀速前进，出界后从左侧回绕。
///
/// 它存在的唯一目的，是驱动执行计划 Stage 2 的 WalkingSkeleton，验证三件
/// **不能靠推理确定、只能跑出来**的事：
///   ① SimLoop 独立线程能否与 Godot 主/渲染线程稳定共存（10 分钟长跑）
///   ② MultiMesh 的 2D 实例布局（8 float 变换 + 4 float 颜色）是否正确
///   ③ 每帧「构造实例缓冲 + 提交」的真实 CPU 成本，以及 interop 调用次数
///
/// 阶段 4 汇合时，它会被间隙模型（LineAdvanceSystem）整体替换，
/// 而 RenderBridge 一行都不用改 —— 这正是 RenderSnapshot 这层接口存在的意义。
///
/// ⚠ 这里**故意**每 tick 写满整份快照（O(物品数)）。那是「渲染缓冲构造」的固有成本，
/// 与间隙模型能否把**模拟**做成 O(线数) 无关。两者是两笔独立预算：
/// 模拟 ≤ 8ms、渲染桥 ≤ 2ms。
///
/// 物品按紧凑前缀排列，销毁走 swap-remove —— 见 RenderSnapshot 的说明。
/// </summary>
public sealed class TrivialItemSim : ISimSource
{
    public const float DefaultWorldWidth = 1920f;
    public const float DefaultWorldHeight = 1080f;

    private readonly float[] _baseX;   // 每个物品的基准 x（恒定），实际位置 = baseX + offset（回绕）
    private readonly float[] _y;
    private readonly uint[] _tint;
    private readonly float _speed;

    private float _offset;

    public SnapshotTripleBuffer Buffers { get; }
    public int ItemCount { get; }
    public int Tick { get; private set; }

    public float WorldWidth { get; }
    public float WorldHeight { get; }

    public TrivialItemSim(
        int itemCount,
        int seed = 12345,
        float worldWidth = DefaultWorldWidth,
        float worldHeight = DefaultWorldHeight,
        float speed = 120f)
    {
        if (itemCount <= 0) throw new ArgumentOutOfRangeException(nameof(itemCount));

        ItemCount = itemCount;
        WorldWidth = worldWidth;
        WorldHeight = worldHeight;
        _speed = speed;
        Buffers = new SnapshotTripleBuffer(itemCount);

        _baseX = new float[itemCount];
        _y = new float[itemCount];
        _tint = new uint[itemCount];

        // 确定性：固定种子 ⇒ 同一 seed 下两遍运行结果逐位一致（探针据此验证）
        var rng = new Random(seed);
        for (int i = 0; i < itemCount; i++)
        {
            _baseX[i] = rng.NextSingle() * worldWidth;
            _y[i] = rng.NextSingle() * worldHeight;
            _tint[i] = PackTint(i);
        }
    }

    /// <summary>ISimSource：用构造时设定的速度推进。</summary>
    public void Step(float dt) => Step(dt, _speed);

    /// <summary>推进一个 tick 并发布一帧快照。**只在 Sim 线程调用。**</summary>
    public void Step(float dt, float speed)
    {
        _offset += speed * dt;
        if (_offset >= WorldWidth) _offset -= WorldWidth;   // 单步位移远小于世界宽度，一步回绕足够

        RenderSnapshot s = Buffers.BeginWrite();

        int n = ItemCount;
        float w = WorldWidth;
        for (int i = 0; i < n; i++)
        {
            float x = _baseX[i] + _offset;
            if (x >= w) x -= w;

            int p = i * 2;
            s.Xy[p] = x;
            s.Xy[p + 1] = _y[i];
            s.SpriteId[i] = (ushort)(i & 3);   // 占位：4 种"精灵"，图集接入前只是编号
            s.Tint[i] = _tint[i];
        }

        s.LiveCount = n;
        s.Tick = ++Tick;
        Buffers.EndWrite();
    }

    /// <summary>取最近发布快照的内容哈希（确定性验证用）。仅在无并发写时调用（探针场景）。</summary>
    public ulong SnapshotHash()
    {
        if (!Buffers.TryClaim(out RenderSnapshot s)) return 0;
        ulong h = s.ComputeHash();
        Buffers.Release();
        return h;
    }

    /// <summary>调色板占位：按索引给一个高对比色，方便肉眼判断实例在动、以及布局是否正确。</summary>
    private static uint PackTint(int i)
    {
        uint r = (uint)(60 + (i * 37) % 196);
        uint g = (uint)(60 + (i * 91) % 196);
        uint b = (uint)(60 + (i * 143) % 196);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }
}
