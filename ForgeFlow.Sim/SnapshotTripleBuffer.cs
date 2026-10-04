namespace ForgeFlow.Sim;

/// <summary>
/// 三缓冲发布器：Sim 线程写、Godot 渲染线程读，双方不共享任何可写状态。
///
/// 为什么不是双缓冲：
///   双缓冲下 Sim 刚发布的那块，正是渲染线程可能仍在读的那块；Sim 下一个 tick
///   就会开始覆盖它 —— 典型的撕裂/竞争。用三块 + 「发布 / 认领」两个极小临界区，
///   保证 BeginWrite 返回的缓冲一定不被任何读方持有。
///
/// 为什么用 lock 而不是无锁：
///   临界区只做**下标记账**（纳秒级），**绝不在锁内填充或拷贝数据**。
///   每 tick 一次、每渲染帧一次，争用基本为零。等剖析数据真的指向它再改
///   Interlocked 无锁版本 —— 过早无锁化是纯粹的风险。
///
/// 线程契约（违反即在 Release 下静默出错，因为 Godot 的线程断言宏只在 Debug 编译）：
///   - BeginWrite() / EndWrite() 只在 Sim 线程调用，成对出现，不要跨 tick 持有
///   - TryClaim() / Release() 只在渲染线程调用，成对出现
///   - 读方拿到的缓冲在下标记账上是「被持有」的，写方会跳过它
/// </summary>
public sealed class SnapshotTripleBuffer
{
    private readonly RenderSnapshot[] _buffers;
    private readonly object _gate = new();

    private int _writing;       // Sim 线程当前在写哪块
    private int _ready = -1;    // 最近一次发布完成的下标
    private int _claimed = -1;  // 渲染线程当前持有的下标

    public int Capacity { get; }

    public SnapshotTripleBuffer(int capacity, int bufferCount = 3)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        // ⚠ 必须**至少 3** 个缓冲。2 个看着够（一个写、一个给人读），但 PickFreeLocked
        //   只能排除「已发布」与「被认领」两个下标；当这两个恰好就是全部两个时，
        //   它只能退回 `_writing`，而 `_writing` 正是刚发布出去的那个
        //   ⇒ 下一帧 BeginWrite 会把渲染线程可能还在读的缓冲交回给模拟线程（撕裂）。
        //   3 个才永远找得到一个既没发布、也没被认领的。
        if (bufferCount < 3) throw new ArgumentOutOfRangeException(nameof(bufferCount));

        Capacity = capacity;
        _buffers = new RenderSnapshot[bufferCount];
        for (int i = 0; i < bufferCount; i++) _buffers[i] = new RenderSnapshot(capacity);
        _writing = 0;
    }

    /// <summary>Sim 线程：取一块可写缓冲。填充完毕后必须调用 <see cref="EndWrite"/>。</summary>
    public RenderSnapshot BeginWrite()
    {
        lock (_gate) return _buffers[_writing];
    }

    /// <summary>Sim 线程：发布刚填完的那块，并挑下一块可写的。</summary>
    public void EndWrite()
    {
        lock (_gate)
        {
            _ready = _writing;
            _writing = PickFreeLocked();
        }
    }

    /// <summary>
    /// 渲染线程：认领最近发布的快照。返回 false 表示尚无数据可读。
    /// 认领成功后必须调用 <see cref="Release"/> 归还。
    /// </summary>
    public bool TryClaim(out RenderSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_ready < 0)
            {
                snapshot = null!;
                return false;
            }
            _claimed = _ready;
            snapshot = _buffers[_claimed];
            return true;
        }
    }

    /// <summary>渲染线程：归还认领。返回后写方可以重新使用该缓冲。</summary>
    public void Release()
    {
        lock (_gate) _claimed = -1;
    }

    private int PickFreeLocked()
    {
        for (int i = 0; i < _buffers.Length; i++)
        {
            if (i != _ready && i != _claimed) return i;
        }
        // 只有在缓冲数过少且读写同时持有时才会走到这里。保持原下标，
        // 宁可让写方短暂覆盖一帧，也不要抛异常打断模拟。
        return _writing;
    }
}
