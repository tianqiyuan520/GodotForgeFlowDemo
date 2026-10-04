namespace ForgeFlow.Sim;

/// <summary>
/// 世界坐标里的一个轴对齐矩形（像素）。
///
/// **只在 Sim 工程里用，故意不引 Godot 的 Rect2** —— ForgeFlow.Sim 的硬约束是零 Godot 引用，
/// 而可见范围是一个渲染概念、
/// 由 Godot 侧算好后**推**给模拟：模拟只拿四个 float，不认识 Camera2D 或 Transform2D。
/// </summary>
public readonly record struct WorldRect(float LoX, float LoY, float HiX, float HiY)
{
    public bool IsEmpty => HiX <= LoX || HiY <= LoY;

    public bool Intersects(WorldRect o)
        => !(o.LoX > HiX || o.HiX < LoX || o.LoY > HiY || o.HiY < LoY);
}

/// <summary>
/// **可见范围广播器**：渲染线程推、Sim 线程读。
///
/// 为什么需要它：百万实例的快照填充实测是 25~36ms，而其中绝大部分实例根本不在屏幕上。
/// 剔除必须发生在**填充时**（也就是 Sim 线程里），否则省不下那笔钱 ——
/// 所以渲染线程得把「现在能看见哪一块世界」告诉 Sim 线程。
///
/// 为什么用 lock 而不是无锁：
///   与 SnapshotTripleBuffer 同一条理由（见那里的注释）—— 临界区只做四个 float 的**赋值/读取**
///   （纳秒级），每 tick 一次、每帧一次，争用基本为零。
///   ⚠ **不要**为了省一个 lock 去做无锁化：临界区只有四个 float，收益为零，
///   而风险是静默竞态。
///
/// 语义：**这是渲染提示，不是模拟状态**。
///   · 它不进 StateHash、不影响任何决策 ⇒ 同一份存档在不同视口下模拟结果完全相同；
///   · 没有推送过、或 <see cref="FactorySim.CullEnabled"/> = false 时，
///     模拟必须写**全部**实例（行为与引入剔除之前逐位一致）。
/// </summary>
public sealed class ViewportCull
{
    private readonly object _gate = new();

    private float _loX, _loY, _hiX, _hiY;
    private bool _valid;

    /// <summary>世界 → 屏幕的缩放（= 相机 zoom）。用来判亚像素实例，见 <see cref="TryGet(out WorldRect, out float)"/>。</summary>
    private float _scale = 1f;

    /// <summary>渲染线程：推送当前可见的世界矩形，以及**世界 → 屏幕的缩放**（相机 zoom）。</summary>
    public void Set(float loX, float loY, float hiX, float hiY, float scale = 1f)
    {
        lock (_gate)
        {
            _loX = loX; _loY = loY; _hiX = hiX; _hiY = hiY;
            _scale = scale;
            _valid = true;
        }
    }

    /// <summary>Sim 线程：取当前可见矩形。返回 false = 还没有有效推送过（此时**不要**剔除）。</summary>
    public bool TryGet(out WorldRect rect) => TryGet(out rect, out _);

    /// <summary>
    /// Sim 线程：取可见矩形 **和** 世界→屏幕缩放。
    ///
    /// <paramref name="scale"/> 用来判「**这个实例在屏幕上还占得到一个像素吗**」：
    ///   拉远到一定程度后，一格只有零点几个像素 ⇒ 引擎根本**栅格化不出来**
    ///   （用户看到的「画面全部消失」就是这个），但我们还在照传不误。
    ///   实测：百万场景拉远到 0.01 倍时画面全空、却只剩 **9 FPS**，
    ///   因为 250 万个亚像素实例仍然每帧上传。
    ///   ⇒ 亚像素的实例**必须直接剔掉**：反正看不见，省下的是全部上传量。
    /// </summary>
    public bool TryGet(out WorldRect rect, out float scale)
    {
        lock (_gate)
        {
            if (!_valid)
            {
                rect = default;
                scale = 1f;
                return false;
            }
            rect = new WorldRect(_loX, _loY, _hiX, _hiY);
            scale = _scale;
            return true;
        }
    }
}
