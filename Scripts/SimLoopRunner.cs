using System.Diagnostics;
using System.Threading;
using ForgeFlow.Sim;

// =============================================================================
// SimLoopRunner —— 把模拟跑在独立线程上，按固定 tick 步进，与渲染帧率解耦。
//
// 为什么不用 Godot 的 _PhysicsProcess 驱动：
//   _PhysicsProcess 的 tick 率是全局的（影响物理服务），且提高它会牵连
//   max_physics_steps_per_frame（默认 8）导致"时间膨胀"而不是丢步。
//   更重要的是我们希望逻辑频率与渲染频率**完全独立**可控（技术方案 2.6）。
//
// 线程契约（违反在 Release 下是静默数据竞争，因为 Godot 的线程断言宏只在 Debug 编译）：
//   - 本线程**只**写 RenderSnapshot，绝不触碰 Godot 的任何对象、场景树、Resource
//   - 与主线程之间只通过 SnapshotTripleBuffer 交接（下标记账，不含数据拷贝）
// =============================================================================

/// <summary>独立线程上的固定步长模拟循环。</summary>
public sealed class SimLoopRunner
{
    /// <summary>逻辑 tick 率。做成常量便于后续改成可配置项。</summary>
    public const float TickRate = 60f;
    public const float Timestep = 1f / TickRate;

    private readonly ISimSource _sim;

    private Thread? _thread;
    private volatile bool _stop;
    private long _steps;

    public SimLoopRunner(ISimSource sim)
    {
        _sim = sim;
    }

    public ISimSource Sim => _sim;
    public long StepCount => Interlocked.Read(ref _steps);

    /// <summary>启动模拟线程。</summary>
    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ForgeFlowSim",
            // 不设 Priority：让 OS 自行调度。设高会与渲染抢核。
        };
        _thread.Start();
    }

    /// <summary>请求停止并等待线程退出。必须在主线程调用（_ExitTree）。</summary>
    public void Stop(int joinTimeoutMs = 2000)
    {
        _stop = true;
        if (_thread == null) return;
        // ⚠ 只有**确实等到线程退出**才清掉引用。Join 超时说明它还在跑，这时把 _thread 置空有两个后果：
        //   ① 它再也不会被 Join，静默泄漏；
        //   ② `Start()` 的判据只是 `_thread != null` ⇒ 会**再起一个循环**，
        //      两个线程同时推同一个 ISimSource；而且 Start() 还会把 _stop 置回 false，
        //      老线程于是也停不下来。
        if (_thread.Join(joinTimeoutMs)) _thread = null;
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double nextStepSec = 0.0;

        while (!_stop)
        {
            double now = clock.Elapsed.TotalSeconds;

            if (now < nextStepSec)
            {
                // 剩余时间多就真正睡一下，少就自旋。不要无条件忙等烧核
                //（EntJoy 的 worker 自旋实测约占 1 个核当量，没必要再叠一个）。
                double remainMs = (nextStepSec - now) * 1000.0;
                if (remainMs > 2.0) Thread.Sleep(1);
                else Thread.SpinWait(200);
                continue;
            }

            // 节拍重锚：落后超过半秒说明被抢占/挂起了，直接对齐到现在，
            // 不做补步。否则会陷入"越追越慢"的 spiral of death。
            if (nextStepSec < now - 0.5) nextStepSec = now;

            _sim.Step(Timestep);
            Interlocked.Increment(ref _steps);
            nextStepSec += Timestep;
        }
    }
}
