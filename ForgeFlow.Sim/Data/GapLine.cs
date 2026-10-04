namespace ForgeFlow.Sim.Data;

/// <summary>
/// 一条传送带。**1-lane，Satisfactory 式**：一端进、一端出，分拣/合流由独立的机器承担。
/// 这样做的好处是每条带完全独立 ⇒ 并行时写入互不相交 ⇒ 不需要任何合并就能确定性。
///
/// ─────────────────────────────────────────────────────────────────────
/// ## 间隙模型（来自 Factorio FFF-176；theor 的 Unity DOTS 实现有可读代码）
///
/// **不存物品的绝对位置，只存物品之间的距离。** 物品按**反序**存放：
/// 逻辑下标 0 = 最靠近出口的物品。
///
///     gap[0]  = 出口到物品 0 的距离
///     gap[i]  = 物品 i-1 到物品 i 的距离（i ≥ 1）
///     TailGap = 入口到最后一个物品的距离
///
/// **不变式：sum(gap) + TailGap == LengthSub**（绝对位置 = gap 的前缀和）
///
/// ## O(1) 的来源
///
/// 自由流动时**只有 gap[0] 需要减 1**，其余 gap 全部不动 —— 因为所有物品一起前进了
/// 1 个子格，相对间距不变。再加 TailGap += 1，于是每 tick 只碰两个整数。
/// 这就是 Factorio 的「incrementing/decrementing two integers instead of incrementing
/// the position of all 200 items」。
///
/// 堵塞时（gap[0] 已经是 0 且下游不收）需要找「第一个还能动的物品」=
/// 第一个 gap[i] > 阈值的 i，只减这一个 gap。
///
/// ## 摊还 O(1)：压缩单调性 + 缓存游标（阶段 B4，已实现）
///
/// 朴素写法每 tick 都从下标 0 起扫描找那个 i —— **堵塞时这就是 O(物品数)，
/// 而且那条带其实一件都没动**，等于每 tick 白扫一遍全部物品。
/// 本项目实测（1,000 件/带 × 1000 带，完全堵塞）：6.4 → 0.008 ms/tick。
///
/// 解法是 **<see cref="_cursor"/>（压缩游标）**，靠的是「压缩单调性」：
/// 一旦 gap[i] 被压到阈值，在**没有取出/插入**的前提下它不会自己重新变大
/// ⇒ 「前 k 个都动不了」这个事实**一旦成立就继续成立**。
/// 于是每次扫描不必从 0 起，从上次的位置起即可：
///
///   · 自由流动        ：游标恒为 0，扫描恒为 1 次（与朴素版一致，无退化）
///   · 部分堵塞        ：游标停在第一个还能动的物品上，之后每 tick 只检查它
///   · 完全压死        ：第一次扫到尾部后游标 = Count ⇒ 之后每 tick **0 次**检查
///   · 下游重新开动     ：取出会把游标**归零**，而下标 0 在取出后必然可动
///                     （新 gap[0] = 旧 gap[1] ≥ MinGapSub > 0）⇒ 恢复也是 O(1)
///
/// 游标只在**取出**时归零；**插入**不动它（插入只追加在尾部，
/// 不改变游标之前的任何 gap，因此「前 k 个都动不了」依然成立）。
///
/// ⚠ 安全前提：**所有**改变 gap 的操作都必须走 <see cref="TryInsert"/> /
///   <see cref="TryRemoveAtExit"/>；绕过它们直接改槽位会让游标静默失效。
///
/// ## 阈值与步长
///     i == 0：阈值为 0      —— 物品 0 可以一直走到出口
///     i >  0：阈值为 MinGapSub —— 一个物品的占位，保证不重叠
///     步长固定 1 子格/tick（外部按速度决定每 tick 调几次 AdvanceOneSub）
/// 因为步长是 1、阈值是整数，gap 恰好落在阈值上而不会越过 —— 与参考实现逐位等价。
/// </summary>
public sealed class GapLine
{
    /// <summary>环形槽位。下标 = (Head + 逻辑下标) % Capacity。</summary>
    private readonly ItemSlot[] _slots;

    /// <summary>环形起点：逻辑下标 0（最靠近出口）所在的物理下标。</summary>
    private int _head;

    /// <summary>
    /// **压缩游标**：下标 &lt; 它 的物品**全部压死**（gap ≤ 阈值，本 tick 动不了），
    /// 所以扫描可以从这里起，不必从 0 起。见类型注释的「摊还 O(1)」一节。
    ///
    /// 它是「压缩单调性」的直接产物：只有在**取出**（下游腾空）时才需要归零，
    /// 因为取出会让新的 gap[0] 变大 ⇒ 下标 0 立刻又可动了。
    /// 插入只追加在尾部，不改变它之前的 gap ⇒ 不动游标。
    /// </summary>
    private int _cursor;

    public int Capacity => _slots.Length;

    /// <summary>带的长度，单位子格。</summary>
    public int LengthSub { get; }

    /// <summary>一个物品占位（最小间距），单位子格。等于「1 格 = SubdivPerCell 子格」时的 SubdivPerCell。</summary>
    public int MinGapSub { get; }

    /// <summary>入口到最后一个物品的距离。</summary>
    public int TailGapSub { get; private set; }

    public int Count { get; private set; }

    /// <summary>累计插入 / 取出，供探针校验吞吐。</summary>
    public long InsertedTotal { get; private set; }
    public long RemovedTotal { get; private set; }

    /// <summary>
    /// 上一次 <see cref="AdvanceOneSub"/> 的**逻辑**扫描长度（从下标 0 算起），
    /// 也就是「朴素实现在这条带上要检查几个 gap」。
    /// **这是判断要不要做 B4 摊还优化的直接依据**：自由流动时应恒为 1。
    /// 注意它**不因摊还优化而变小**（堵塞时仍报 n），否则探针就无法分辨
    /// 「场景真的压到了堵塞路径」与「场景空跑」。
    /// </summary>
    public int LastScanLength { get; private set; }

    /// <summary>
    /// 上一次 <see cref="AdvanceOneSub"/> **实际检查了几个 gap**。
    /// 与 <see cref="LastScanLength"/> 的差值就是摊还优化省掉的工作量：
    /// 完全压死的带上它是 0（一个都不看），自由流动时两者都是 1。
    /// </summary>
    public int LastWorkLength { get; private set; }

    /// <summary>
    /// false = **关掉压缩游标**，每 tick 都从下标 0 起扫（摊还优化之前的行为）。
    ///
    /// 只用于**反例控制**（探针 13）：压缩游标是纯优化，关掉它**结果本来就该完全一致**，
    /// 所以「关掉后结果不同」不是有效的反例判据；有效的判据是「关掉后必须显著变慢」——
    /// 若几乎不变慢，说明游标根本没生效，那个「优化」是幻觉。
    /// 与本项目既有的 <c>PowerIncremental</c> / <c>WakeEdgesEnabled</c> 同类。
    /// </summary>
    public bool CursorEnabled { get; set; } = true;

    public GapLine(int capacity, int lengthSub, int minGapSub)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (minGapSub <= 0) throw new ArgumentOutOfRangeException(nameof(minGapSub));
        if (lengthSub < minGapSub) throw new ArgumentOutOfRangeException(nameof(lengthSub));

        _slots = new ItemSlot[capacity];
        LengthSub = lengthSub;
        MinGapSub = minGapSub;
        TailGapSub = lengthSub;   // 空带：整条都是入口端的空闲
        _head = 0;
        Count = 0;
    }

    // ── 只读访问 ────────────────────────────────────────────────────────────

    private int Ring(int logical)
    {
        int i = _head + logical;
        return i < _slots.Length ? i : i - _slots.Length;
    }

    public ushort TypeOf(int logical) => _slots[Ring(logical)].Type;

    public int GapAt(int logical) => _slots[Ring(logical)].Gap;

    /// <summary>物品 logical 距出口的绝对距离（子格）。前缀和 —— 只在渲染与探针里用，不在热路径。</summary>
    public int AbsolutePos(int logical)
    {
        int sum = 0;
        for (int k = 0; k <= logical; k++) sum += _slots[Ring(k)].Gap;
        return sum;
    }

    /// <summary>不变式自检：sum(gap) + TailGap 必须等于 LengthSub。返回差值为 0 才算对。</summary>
    public int InvariantResidual()
    {
        int sum = TailGapSub;
        for (int k = 0; k < Count; k++) sum += _slots[Ring(k)].Gap;
        return sum - LengthSub;
    }

    // ── 操作 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把整条带向前推进 1 个子格。返回是否真的动了。
    ///
    /// 实现就是模型的全部：找到**第一个还能动的物品**，只减它前面那个 gap。
    /// 扫描从 <see cref="_cursor"/> 起（不是从 0 起）—— 见类型注释的「摊还 O(1)」。
    /// </summary>
    public bool AdvanceOneSub()
    {
        int n = Count;
        // 反例控制：关掉游标时恒从下标 0 起扫（摊还优化之前的行为）。
        // 游标**照常维护**，所以重新打开时立刻就是正确的。
        int start = CursorEnabled ? _cursor : 0;

        // ★ 摊还的关键：游标之前的物品**全部压死**，直接跳过。
        //   完全堵死的带满足 start == n ⇒ 循环一次都不进，每 tick 0 次实际检查。
        for (int i = start; i < n; i++)
        {
            int threshold = i == 0 ? 0 : MinGapSub;
            if (_slots[Ring(i)].Gap > threshold)
            {
                LastWorkLength = i - start + 1;   // 实际检查了几个（摊还收益的证据）
                LastScanLength = i + 1;           // 逻辑扫描长度：从 0 算起，与朴素实现同口径
                _slots[Ring(i)].Gap -= 1;
                TailGapSub += 1;
                _cursor = i;          // 下次从同一个位置起就够了（它尚未压到阈值）
                return true;
            }

            // 这一个也压死了 ⇒ 压缩单调性允许把游标推过去，下次不再看它。
            _cursor = i + 1;
        }

        // 没动。⚠ 这里的两个指标**故意不同**：
        //   LastScanLength 仍报 n —— 它是「这条带被压到多深」的观测量
        //     （探针 01 靠 `> 1` 判断场景真的压到了堵塞路径；报 0 会让堵塞场景
        //      静默退化成「看起来和畅通一样」，正是本项目最防的假绿）。
        //   LastWorkLength 报实际检查数（全压死时是 0）—— 摊还收益由此可测。
        LastWorkLength = n - start;
        LastScanLength = n;
        return false;
    }

    /// <summary>入口端能不能再收下一个物品（容量与最小间距都满足）。</summary>
    public bool CanAccept => Count < Capacity && TailGapSub >= MinGapSub;

    /// <summary>
    /// 在入口插入一个物品。入口端必须有 ≥ MinGapSub 的空闲。
    /// 新物品成为最后一个（下标 Count），它与前一个物品的 gap = TailGap；随后 TailGap 归零。
    /// </summary>
    public bool TryInsert(ushort type)
    {
        if (Count >= Capacity) return false;
        if (TailGapSub < MinGapSub) return false;

        int idx = Count;
        int r = Ring(idx);
        _slots[r].Type = type;
        _slots[r].Gap = TailGapSub;
        Count = idx + 1;
        TailGapSub = 0;

        // 不动游标。不变式是 `_cursor <= Count`，插入后新物品落在 idx 上，
        // 而 `_cursor <= idx` ⇒ 游标最多正好指向新物品（它 gap = 插入前的 TailGap
        // ≥ MinGapSub > 阈值 ⇒ 必然可动，下次扫描就会推它）。见类型注释。
        InsertedTotal++;
        return true;
    }

    /// <summary>
    /// 从出口取出一个物品。只有当 gap[0] == 0（物品 0 已经走到出口）时才成立。
    ///
    /// 取出后其余物品的**绝对位置不变**：新的物品 0 是原来的物品 1，
    /// 它距出口的距离 = 旧 gap[0] + 旧 gap[1]。
    /// </summary>
    public bool TryRemoveAtExit(out ItemSlot removed)
    {
        if (Count == 0) { removed = default; return false; }
        if (_slots[_head].Gap > 0) { removed = default; return false; }

        int g0 = _slots[_head].Gap;
        removed = _slots[_head];

        _head += 1;
        if (_head >= _slots.Length) _head = 0;
        Count -= 1;
        RemovedTotal++;

        if (Count == 0)
        {
            // 带空了：整条都变回入口端空闲
            TailGapSub = LengthSub;
        }
        else
        {
            _slots[_head].Gap += g0;
        }

        // ★ 取出让新的 gap[0] 变大（= 旧 gap[0] + 旧 gap[1]，且旧 gap[1] ≥ MinGapSub）
        //   ⇒ 下标 0 必然重新可动，所以游标必须归零，否则会漏掉它。
        //   这也是「解除堵塞」能从 O(1) 恢复的原因 —— 恢复点固定在下标 0。
        _cursor = 0;
        return true;
    }

    /// <summary>
    /// 一个 tick 的完整推进。三步顺序固定，参考实现必须用同一顺序才能逐位比对。
    ///
    ///   1. 出口取出（若下游可收）
    ///   2. 前进 1 子格
    ///   3. 入口插入（若上游有料）
    /// </summary>
    public void Step(bool sinkCanAccept, bool sourceHasItem, ushort incomingType)
    {
        if (sinkCanAccept) TryRemoveAtExit(out _);
        AdvanceOneSub();
        if (sourceHasItem) TryInsert(incomingType);
    }

    /// <summary>清空（重建场景用）。</summary>
    public void Clear()
    {
        Count = 0;
        _head = 0;
        _cursor = 0;          // ★ 必须与 Count 一起复位，否则清空后第一件物品推不动
        TailGapSub = LengthSub;
        InsertedTotal = 0;
        RemovedTotal = 0;
    }
}
