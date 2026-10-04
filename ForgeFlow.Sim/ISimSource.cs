namespace ForgeFlow.Sim;

/// <summary>
/// 模拟数据源：产出渲染快照。
///
/// 桥接层（Godot 侧）只认这个接口与 <see cref="RenderSnapshot"/>，
/// 不知道位置是怎么算出来的 —— 这是把框架/算法做成「可换入件」的关键一层。
///
/// 当前有两个实现：
///   - <see cref="TrivialItemSim"/>：占位匀速直线运动，用于验证线程与渲染通路
///   - <see cref="BeltSim"/>：间隙模型驱动，真正的传送带
/// 两者在 Godot 侧可以经环境变量互换，而 RenderBridge 一行都不用改。
/// </summary>
public interface ISimSource
{
    /// <summary>三缓冲发布器。写方在此发布快照，渲染线程认领。</summary>
    SnapshotTripleBuffer Buffers { get; }

    /// <summary>推进一个逻辑 tick。**只在 Sim 线程调用。**</summary>
    void Step(float dt);
}
