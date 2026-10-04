namespace ForgeFlow.Sim.Data;

/// <summary>
/// 机器的三阶段状态机。
///
/// 只有 <see cref="Working"/> 的机器每 tick 都要推进，另外两个状态**可以睡眠** ——
/// 这是「活跃集」能省下工作的前提（对应 Factorio 的 Roboport 优化：
/// 「most Roboports don't really do anything except exist」，关掉后 1ms → 0.025ms/tick）。
///
/// 唤醒必须由显式的**唤醒边**触发，见 FactorySim.EvaluateWakeEdges。
/// 漏掉一条唤醒边 = 工厂静默卡死（玩家看到东西不动但没有任何报错），
/// 所以本项目用「休眠模式 vs 全量模式逐 tick 比对」来系统性防它。
/// </summary>
public enum MachineState : byte
{
    /// <summary>没有输入可吃（或无输入带但尚未开工）。**可睡眠**，等 W1 唤醒。</summary>
    WaitingInput = 0,

    /// <summary>加工中。**必须保持活跃**，每 tick 递减进度。</summary>
    Working = 1,

    /// <summary>加工完了但输出带满。**可睡眠**，等 W2 唤醒。</summary>
    BlockedOutput = 2,
}
