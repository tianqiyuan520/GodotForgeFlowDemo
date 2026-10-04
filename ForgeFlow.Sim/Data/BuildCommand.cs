namespace ForgeFlow.Sim;

/// <summary>
/// 建造命令 —— **从 UI/渲染线程发往模拟线程的消息**。
///
/// ⚠ 为什么必须是「命令 + 队列」，而不是直接改布局：
///
///   1. **线程安全**：模拟跑在独立线程上（<c>SimLoopRunner</c>），UI 线程直接改布局
///      就是和 tick 抢同一份数据 —— 轻则读到半更新状态，重则集合被边遍历边改。
///   2. **原子性**：一次建造往往不止一步（加机器 + 连带 + 重建电网）。
///      直接改的话，模拟可能在这两步之间 tick 一次，读到一个「机器在但带没接上」的
///      不一致拓扑。排队在 tick 边界一次性应用，就天然没有这个问题。
///   3. **确定性**：同一串命令、同一个起始状态 ⇒ 同一个结果。
///      这对「重放 / 存档 / 联机」都是前提，而且几乎不要钱 —— 命令是有序队列。
///
/// 命令只是**意图**，不是保证：模拟线程会按当前占用位图校验，失败就丢弃并记原因
/// （见 <see cref="FactorySim.LastBuildResult"/>）。校验必须在模拟线程做 ——
/// UI 侧的预检只是为了给玩家即时反馈，不能作为依据。
/// </summary>
public abstract record BuildCommand;

/// <summary>
/// 放一台机器。**命令携带的是「可建造项」而不是一堆散参数** ——
/// 这样「这台机器是干什么的、长什么样、占几格」全部来自内容表，命令本身不复制一遍。
/// </summary>
/// <param name="Buildable">内容表里的一行（探针也用这个，走 <see cref="BuildableDef.AdHoc"/>）。</param>
/// <param name="Pos">占用范围的左上格。</param>
/// <param name="Facing">朝向 0..3（纯外观）。</param>
/// <param name="NetworkId">电网编号；-1 = 新拉一条电网。</param>
public sealed record BuildMachineCommand(
    BuildableDef Buildable,
    GridPos Pos,
    int Facing = 0,
    int NetworkId = -1) : BuildCommand
{
    /// <summary>
    /// 便捷构造：按机器种类现造一个**无配方**的临时可建造项。
    ///
    /// ⚠ 这是给**探针与演示布局**留的兼容入口：它走的是引入配方之前的透传路径，
    /// 所以 probe 03/06/07 的数值仍是有效的回归锚点。游戏里的建造一律走内容表。
    /// </summary>
    public BuildMachineCommand(MachineKind kind, GridPos Pos, int workTicks,
                               int NetworkId = -1, int SizeX = 1, int SizeY = 1)
        : this(BuildableDef.AdHoc(kind, workTicks, SizeX, SizeY), Pos, 0, NetworkId)
    {
    }
}

/// <summary>拆掉某一格上的东西（机器或整条带）。</summary>
public sealed record RemoveAtCommand(GridPos Pos) : BuildCommand;

/// <summary>
/// 拆掉矩形区域 <paramref name="A"/>..<paramref name="B"/>（含端点）里的一切。
///
/// 规则与 <see cref="CopyAreaCommand"/> **对齐**（玩家只需要记一套）：
///   · 机器按**左上角是否落在框内**决定拆不拆，不做「部分选中」的裁剪；
///   · 带 / 管道只要**有一格**落在框内就**整段拆掉** —— 与单击拆除同语义（单击拆一格也是拆整段）。
///
/// 整个区域算**一次动作**：撤销时一次 Ctrl+Z 把整片恢复回来（见 FactorySim 的区域撤销组）。
/// </summary>
public sealed record RemoveAreaCommand(GridPos A, GridPos B) : BuildCommand;

/// <summary>
/// 从 <paramref name="From"/> 直线铺管道到 <paramref name="To"/>。
///
/// 注意语义：**管网是按连通分量算的，不由「一次铺管」决定**。
/// 分两次铺的管道只要边相邻就会合成同一个管网。
/// </summary>
public sealed record BuildPipeCommand(GridPos From, GridPos To) : BuildCommand;

/// <summary>
/// 从 <paramref name="From"/> 直线铺带到 <paramref name="To"/>（含两端点，允许单格）。
/// 只能横或竖，斜线会被拒绝 —— 自动铺瓦会按路径方向选直线/拐角瓦片。
/// </summary>
/// <param name="Level">
/// **铺在哪一层**：0 = 低轨（默认），1 = 高轨。见 <see cref="BeltDef.Level"/>。
/// 不同层的带可以共用同一格（交错），同层不行 —— 校验在模拟线程的
/// <see cref="OccupancyGrid.CanPlaceBelt(GridPos, int)"/>。
/// </param>
public sealed record BuildBeltCommand(GridPos From, GridPos To, int Level = 0) : BuildCommand;

/// <summary>机器绑轨道时扮演的角色。</summary>
public enum TrackRole : byte
{
    /// <summary>绑成**输入轨**（这台机器从这条带取料）。</summary>
    Input = 0,

    /// <summary>绑成**输出轨**（这台机器往这条带送货）。</summary>
    Output = 1,

    /// <summary>**解除**绑定，回到几何邻接推导（<see cref="BeltId"/> 给定时只解除这条带的）。</summary>
    Clear = 2,
}

/// <summary>
/// 把机器 <paramref name="MachineCell"/> 上那台机器的某个角色绑定到 <paramref name="BeltId"/>。
///
/// · <see cref="TrackRole.Input"/> / <see cref="TrackRole.Output"/> + 合法的 beltId ⇒ 建立绑定；
/// · <see cref="TrackRole.Clear"/> ⇒ 解除（beltId 给定时只解除指向它的那些角色，-1 = 全解除）。
///
/// 为什么按**格子**而不是机器 id 传：与其它建造命令一致（UI 只有格子，id 会随拆除失效），
/// 而且玩家点的就是他看到的那个东西。
/// </summary>
public sealed record BindMachineTrackCommand(GridPos MachineCell, TrackRole Role, int BeltId = -1)
    : BuildCommand;

/// <summary>
/// 撤销上一步建造 / 拆除。**走命令队列**，所以撤销本身也是 tick 边界原子生效的 ——
/// 不需要为它开一条「直接改布局」的旁路，也就不会绕开占用位图与确定性。
/// </summary>
public sealed record UndoCommand : BuildCommand;

/// <summary>重做（撤销的反向）。</summary>
public sealed record RedoCommand : BuildCommand;

/// <summary>
/// 把矩形区域 <paramref name="A"/>..<paramref name="B"/>（含端点）里的一切收进剪贴板。
///
/// 收的是**相对坐标**（相对左上角），所以粘贴到别处不会带着原位置走。
/// 带的**路径方向**也保留 —— 粘贴出来的带要和原件一样，而不是简化成直线。
/// </summary>
public sealed record CopyAreaCommand(GridPos A, GridPos B) : BuildCommand;

/// <summary>
/// 把剪贴板里的东西贴到 <paramref name="Origin"/>（对应复制的左上角）。
/// 与已有东西重叠的格子会被**跳过并计数**，不整体失败 —— 用蓝图贴一片时
/// 总会有几格压着，整体拒绝会让这个功能没法用。
/// </summary>
public sealed record PasteCommand(GridPos Origin, bool WithBelts = true) : BuildCommand;

/// <summary>剪贴板里的一台机器：**相对坐标** + 原始定义（粘贴时按它复制一份）。</summary>
public readonly record struct BlueprintMachine(GridPos Rel, MachineDef Def);

/// <summary>一次建造尝试的结果。UI 用它给玩家反馈，探针用它做断言。</summary>
public enum BuildOutcome
{
    /// <summary>没有任何待处理命令。</summary>
    None = 0,
    /// <summary>命令被接受并生效。</summary>
    Placed = 1,
    /// <summary>目标格已被占用。</summary>
    Occupied = 2,
    /// <summary>越出网格范围。</summary>
    OutOfBounds = 3,
    /// <summary>目标格上没有可拆的东西。</summary>
    NothingThere = 4,
    /// <summary>斜线 / 空范围等非法形状。</summary>
    BadShape = 5,
    /// <summary>带太长（会超出快照容量）。</summary>
    TooLong = 6,
    /// <summary>没有可撤销 / 可重做的动作。</summary>
    NothingToDo = 7,
}

/// <summary>建造命令的执行结果（模拟线程写入，UI 线程读取展示）。</summary>
public readonly record struct BuildResult(BuildOutcome Outcome, GridPos Pos, string Detail)
{
    public bool Ok => Outcome == BuildOutcome.Placed;
}
