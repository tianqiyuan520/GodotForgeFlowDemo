namespace ForgeFlow.Sim;

/// <summary>
/// 渲染快照：Sim 线程产出、Godot 主/渲染线程消费。
///
/// 桥接层只认这个结构，它不知道位置是「占位模拟」还是「间隙模型」算出来的。
/// 这是把框架做成「可换入件」的关键一层：
/// 阶段 4 把占位模拟换成间隙模型时，RenderBridge 一行都不用改。
///
/// ⚠ 物品按**紧凑前缀**存放：存活物品必须落在 [0, LiveCount)。
/// 原因：Godot 的 MultiMesh.VisibleInstanceCount 只截断**前缀**，且不改 buffer 尺寸
/// （官方推荐的分配模式是启动时设 InstanceCount = MAX，之后只改可见数）。
/// 因此销毁物品必须用 swap-remove 保持紧凑 —— 这条纪律会一路约束到间隙模型的设计。
///
/// 长度全部按 Capacity 预分配，运行期零分配（避免 GC 停顿落到逻辑帧上）。
/// </summary>
public sealed class RenderSnapshot
{
    /// <summary>本帧存活物品数，即要渲染的实例数。写入方必须在填完后设置。</summary>
    public int LiveCount;

    /// <summary>产出它的逻辑 tick 号，供插值与探针对齐。</summary>
    public int Tick;

    /// <summary>物品位置，长度 = 2 * Capacity，布局 [x0, y0, x1, y1, ...]（世界单位）。</summary>
    public readonly float[] Xy;

    /// <summary>精灵索引：进图集的稳定编号（图集尚未接入时是占位值）。</summary>
    public readonly ushort[] SpriteId;

    /// <summary>实例染色，0xAARRGGBB。占位美术策略就是「1x1 白纹理 + 每实例颜色」。</summary>
    public readonly uint[] Tint;

    /// <summary>
    /// 逐实例尺寸倍率（乘在桥接层的基础边长上）。默认 1。
    /// 用于让机器与物品共用一个 MultiMesh 而尺寸不同（机器更大）。
    /// 若将来把机器拆到独立的 MultiMeshInstance2D 层，这个字段就可以去掉。
    /// </summary>
    public readonly float[] Scale;

    /// <summary>
    /// 逐实例的**带面滚动相位**（0..1），负数表示该实例不滚动。
    /// 这套美术的传送带是静态瓦片，流动感只能靠滚动 UV —— 相位由模拟按「带子实际走了多远」算出，
    /// 所以画面速度与逻辑速度天然一致。
    ///
    /// ⚠ 相位是**带符号的**：反向的带（cells[0] 在西端却往东流…即流向为负轴）用 `1 - p`
    ///   等价于把纹路反向（<c>fract</c> 会自动回卷），所以纹路永远跟着物品走。
    /// </summary>
    public readonly float[] Scroll;

    /// <summary>
    /// 逐实例的**带面流向轴**：+1 = 沿 X 滚（横向带），-1 = 沿 Y 滚（纵向带），0 = 不是带面。
    ///
    /// 为什么必须有它：着色器只有 4 个 custom_data 分量，带面又永远是整格取样（两轴缩放恒为 1），
    /// 于是把「滚哪个轴」塞进 X 向取样缩放那一分量的**符号**里。少了它，纵向带会被横向平移，
    /// 整格瓦片每帧错开 p 个格宽（症状：纵向带的绿色路轨跑到格子中间、断成一节一节）。
    /// </summary>
    public readonly float[] ScrollAxis;

    /// <summary>
    /// 逐实例的旋转角（弧度，绕实例中心）。机器朝向用它。
    /// 只有机器会用到；带面/物品恒为 0。
    /// </summary>
    public readonly float[] Rot;

    public int Capacity => SpriteId.Length;

    public RenderSnapshot(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Xy = new float[capacity * 2];
        SpriteId = new ushort[capacity];
        Tint = new uint[capacity];
        Scale = new float[capacity];
        Scroll = new float[capacity];
        ScrollAxis = new float[capacity];      // 默认 0 = 非带面
        Rot = new float[capacity];
        Array.Fill(Scale, 1f);    // 默认倍率 1；不能留 0，否则实例不可见
        Array.Fill(Scroll, -1f);  // 默认不滚动
    }

    /// <summary>
    /// 内容哈希，用于**确定性验证**（同一输入序列跑两遍必须完全一致）。
    /// 只覆盖 LiveCount 内的有效数据，忽略 Capacity 之后的垃圾。
    /// </summary>
    public ulong ComputeHash()
    {
        ulong h = 1469598103934665603UL;                       // FNV-1a 64
        void Mix(ulong v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }

        Mix((ulong)LiveCount);
        Mix((ulong)Tick);
        for (int i = 0; i < LiveCount; i++)
        {
            Mix((ulong)BitConverter.SingleToInt32Bits(Xy[i * 2]));
            Mix((ulong)BitConverter.SingleToInt32Bits(Xy[i * 2 + 1]));
            Mix(SpriteId[i]);
            Mix(Tint[i]);
            Mix((ulong)BitConverter.SingleToInt32Bits(Scale[i]));
            Mix((ulong)BitConverter.SingleToInt32Bits(Scroll[i]));
            Mix((ulong)BitConverter.SingleToInt32Bits(ScrollAxis[i]));
            Mix((ulong)BitConverter.SingleToInt32Bits(Rot[i]));
        }
        return h;
    }
}
