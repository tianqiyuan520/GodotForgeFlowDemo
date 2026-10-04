namespace ForgeFlow.Sim.Data;

/// <summary>
/// 传送带上的一个物品槽位。
///
/// 字段刻意压到最小：一个类型（只给渲染用）+ 一个间隙（模拟的全部状态）。
/// **注意这里没有"位置"字段** —— 物品的绝对位置是 gap 的前缀和，不单独存。
/// 这正是间隙模型能把每 tick 的工作量从 O(物品数) 降到 O(1) 的原因。
/// </summary>
public struct ItemSlot
{
    /// <summary>物品类型（进图集的稳定编号）。0 保留为"空"，使 default(ItemSlot) 天然无效。</summary>
    public ushort Type;

    /// <summary>
    /// 与相邻物品（下标 0 则是与出口）之间的距离，单位是**子格**（subdiv）。
    /// 语义详见 <see cref="GapLine"/>。
    /// </summary>
    public int Gap;
}
