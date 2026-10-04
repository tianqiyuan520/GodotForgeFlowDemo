using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

/// <summary>
/// 用**间隙模型**驱动的传送带场景：若干条水平带并排，物品从入口流向出口。
///
/// 这是执行计划 Stage 3 的核心：<see cref="TrivialItemSim"/> 被它替换，
/// 而 Godot 侧的 <c>RenderBridge</c> 一行都不用改 —— 两个都只产出
/// <see cref="RenderSnapshot"/>。
///
/// ⚠ 渲染侧固有成本：要拿到物品的**绝对位置**必须走 gap 的前缀和，
/// 所以这一段天然是 O(可见物品数)。间隙模型让**模拟**变成 O(线数)，
/// 但**渲染缓冲构造**仍然是 O(物品数) —— 两者是两笔独立预算
/// （模拟 ≤ 8ms、渲染桥 ≤ 2ms）。
///
/// 1-lane Satisfactory 模型：一条带只有一进一出。⇒ **没有跨带移交**，
/// 原计划 B5 的「跨界移交」在 v1 下不需要；物品到达出口即交给下游机器
/// （v1 里直接丢弃，阶段 2 才接上机器）。
/// </summary>
public sealed class BeltSim : ISimSource
{
    /// <summary>
    /// 1 格 = 多少子格。同时也就是「一个物品的占位」（最小间距）。
    /// **与模拟主干共用同一个常量**（<c>FactorySim.SubdivPerCell</c>）。
    /// </summary>
    public const int SubdivPerCell = FactorySim.SubdivPerCell;
    public const int MinGapSub = FactorySim.MinGapSub;

    /// <summary>1 格在世界里多少像素。同样直接引用主干的值。</summary>
    public const float CellSizePx = FactorySim.CellSizePx;

    public const float MarginX = 40f;

    private const float PxPerSub = CellSizePx / SubdivPerCell;   // 每子格多少像素

    private readonly GapLine[] _lines;
    private readonly float[] _x0;        // 各线入口端的世界坐标
    private readonly float[] _y0;
    private readonly uint[] _tint;
    private readonly ushort[] _nextType;

    private readonly int _speedSub;
    private readonly int _typeCount;
    private readonly int _insertEveryTicks;

    public SnapshotTripleBuffer Buffers { get; }
    public int LineCount => _lines.Length;
    public int CellsPerLine { get; }
    public int CapacityPerLine { get; }

    /// <summary>每 tick 推进多少子格。</summary>
    public int SpeedSub => _speedSub;

    public long StepCount { get; private set; }
    public int LiveCount { get; private set; }

    /// <summary>全场景累计插入 / 取出（由各条带的 GapLine 汇总，按需计算，不在热路径）。</summary>
    public (long Inserted, long Removed) Totals()
    {
        long ins = 0, rem = 0;
        foreach (GapLine l in _lines) { ins += l.InsertedTotal; rem += l.RemovedTotal; }
        return (ins, rem);
    }

    /// <param name="lineCount">带的条数（纵向并排）。</param>
    /// <param name="cellsPerLine">每条带多少格；null = 按世界宽度自动算。</param>
    /// <param name="speedSub">每 tick 推进的子格数。吞吐上限 = speedSub / MinGapSub 件每 tick。</param>
    /// <param name="typeCount">物品类型数（占位渲染里映射到不同精灵编号）。</param>
    /// <param name="insertEveryTicks">
    /// 上游每多少 tick 投一份料。设为 1 时上游无限制、带会被灌满（实心流）；
    /// 设得比「吞吐上限的倒数」大，物品就会分离开、能直观看出间隙。
    /// 例：speedSub=4、minGap=16 ⇒ 上限 0.25 件/tick（每 4 tick 一件）；
    /// 取 8 则实际 0.125 件/tick，物品间距约 32 子格 = 2 格。
    /// </param>
    public BeltSim(
        int lineCount,
        int? cellsPerLine = null,
        int speedSub = 4,
        int typeCount = 8,
        float worldWidth = 1920f,
        float worldHeight = 1080f,
        int insertEveryTicks = 8)
    {
        if (lineCount <= 0) throw new ArgumentOutOfRangeException(nameof(lineCount));
        if (speedSub <= 0) throw new ArgumentOutOfRangeException(nameof(speedSub));
        if (typeCount <= 0) throw new ArgumentOutOfRangeException(nameof(typeCount));
        if (insertEveryTicks <= 0) throw new ArgumentOutOfRangeException(nameof(insertEveryTicks));

        CellsPerLine = cellsPerLine ?? (int)((worldWidth - 2f * MarginX) / CellSizePx);
        if (CellsPerLine <= 0) throw new ArgumentOutOfRangeException(nameof(cellsPerLine));

        _speedSub = speedSub;
        _typeCount = typeCount;
        _insertEveryTicks = insertEveryTicks;

        int lengthSub = CellsPerLine * SubdivPerCell;
        CapacityPerLine = CellsPerLine;              // 灌满时的最大件数（每件占 MinGapSub）
        int totalCapacity = CapacityPerLine * lineCount;

        Buffers = new SnapshotTripleBuffer(totalCapacity);

        _lines = new GapLine[lineCount];
        _x0 = new float[lineCount];
        _y0 = new float[lineCount];
        _tint = new uint[lineCount];
        _nextType = new ushort[lineCount];

        float pitch = worldHeight / (lineCount + 1f);
        for (int i = 0; i < lineCount; i++)
        {
            _lines[i] = new GapLine(CapacityPerLine, lengthSub, MinGapSub);
            _x0[i] = MarginX;
            _y0[i] = pitch * (i + 1);
            _tint[i] = RowTint(i, lineCount);
            _nextType[i] = (ushort)(1 + i % typeCount);
        }
    }

    public void Step(float dt)
    {
        RenderSnapshot s = Buffers.BeginWrite();
        float[] xy = s.Xy;
        ushort[] sprite = s.SpriteId;
        uint[] tint = s.Tint;
        int cap = s.Capacity;
        int live = 0;
        bool sourceTick = StepCount % _insertEveryTicks == 0;

        for (int L = 0; L < _lines.Length; L++)
        {
            GapLine line = _lines[L];

            // 一个 tick：下游全收 + 推进 speedSub 个子格 + 入口尝试插入（被 TailGap 门控）
            for (int k = 0; k < _speedSub; k++)
            {
                line.TryRemoveAtExit(out _);
                line.AdvanceOneSub();
            }
            if (sourceTick)
            {
                line.TryInsert(_nextType[L]);
                _nextType[L] = (ushort)(_nextType[L] % _typeCount + 1);
            }

            // 写快照：前缀和一次走完（O(物品数)，这是渲染构造的固有成本）
            int n = line.Count;
            int lengthSub = line.LengthSub;
            float x0 = _x0[L];
            float y = _y0[L];
            uint c = _tint[L];
            int acc = 0;

            for (int i = 0; i < n && live < cap; i++)
            {
                acc += line.GapAt(i);                       // acc = 距出口的距离（子格）
                float t = (lengthSub - acc) * PxPerSub;     // 换算成距入口的像素
                xy[live * 2] = x0 + t;
                xy[live * 2 + 1] = y;
                sprite[live] = line.TypeOf(i);
                tint[live] = c;
                live++;
            }
        }

        s.LiveCount = live;
        s.Tick = (int)(StepCount + 1);
        Buffers.EndWrite();

        StepCount++;
        LiveCount = live;
    }

    /// <summary>供探针/诊断：某条带的累计插入与取出。</summary>
    public (long Inserted, long Removed) LineStats(int index)
        => (_lines[index].InsertedTotal, _lines[index].RemovedTotal);

    /// <summary>供探针/诊断：某条带当前件数与入口端空闲。</summary>
    public (int Count, int TailGapSub, int LengthSub) LineState(int index)
    {
        GapLine l = _lines[index];
        return (l.Count, l.TailGapSub, l.LengthSub);
    }

    /// <summary>每行一个色相，便于肉眼确认「一行是一条独立的带」。</summary>
    private static uint RowTint(int i, int count)
    {
        float h = count <= 1 ? 0f : (float)i / count;
        // HSV(h, 0.55, 0.95) -> RGB，避免引第三方库，手写小段
        float r = 0, g = 0, b = 0;
        float sector = h * 6f;
        int si = (int)sector % 6;
        float f = sector - MathF.Floor(sector);
        float p = 0.95f * (1 - 0.55f);
        float q = 0.95f * (1 - 0.55f * f);
        float t = 0.95f * (1 - 0.55f * (1 - f));
        switch (si)
        {
            case 0: r = 0.95f; g = t; b = p; break;
            case 1: r = q; g = 0.95f; b = p; break;
            case 2: r = p; g = 0.95f; b = t; break;
            case 3: r = p; g = q; b = 0.95f; break;
            case 4: r = t; g = p; b = 0.95f; break;
            default: r = 0.95f; g = p; b = q; break;
        }
        return 0xFF000000u
             | ((uint)(r * 255f) << 16)
             | ((uint)(g * 255f) << 8)
             | (uint)(b * 255f);
    }
}
