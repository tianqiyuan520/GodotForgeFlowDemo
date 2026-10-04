namespace ForgeFlow.Sim.Reference;

/// <summary>
/// 朴素参考实现：直接用**绝对位置**表示物品，每 tick 遍历并更新全部物品。
/// 慢得多，但显然正确 —— 它唯一的用途是给间隙模型（<see cref="Data.GapLine"/>）
/// 做**逐 tick 逐项比对**的基准。
///
/// 纪律（止损点 S4）：**没有参考实现就不要往下加功能。**
/// 间隙模型的所有不变量（压缩、取出后位置不变、入口插入）都只能靠它系统性地证伪。
///
/// 语义与 GapLine 逐条对齐：
///   - 物品按距出口的距离 **升序**存放，下标 0 = 最靠近出口
///   - 每 tick 顺序：① 出口取出 ② 全部物品尝试前进 1 子格 ③ 入口插入
///   - 前进受「不能越过前一个物品 + MinGapSub」和「不能越过出口（0）」约束
/// </summary>
public sealed class ReferenceLine
{
    private readonly int[] _d;        // 距出口的绝对距离（子格），升序
    private readonly ushort[] _type;
    private int _n;

    public int LengthSub { get; }
    public int MinGapSub { get; }
    public int Capacity => _d.Length;
    public int Count => _n;

    public long InsertedTotal { get; private set; }
    public long RemovedTotal { get; private set; }

    public ReferenceLine(int capacity, int lengthSub, int minGapSub)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (minGapSub <= 0) throw new ArgumentOutOfRangeException(nameof(minGapSub));
        if (lengthSub < minGapSub) throw new ArgumentOutOfRangeException(nameof(lengthSub));
        _d = new int[capacity];
        _type = new ushort[capacity];
        LengthSub = lengthSub;
        MinGapSub = minGapSub;
        _n = 0;
    }

    public int PosAt(int i) => _d[i];
    public ushort TypeAt(int i) => _type[i];

    /// <summary>见 <see cref="Data.GapLine.Step"/>：三步顺序必须完全一致。</summary>
    public void Step(bool sinkCanAccept, bool sourceHasItem, ushort incomingType)
    {
        // ① 出口取出
        if (sinkCanAccept)
        {
            while (_n > 0 && _d[0] <= 0)
            {
                Array.Copy(_d, 1, _d, 0, _n - 1);
                Array.Copy(_type, 1, _type, 0, _n - 1);
                _n--;
                RemovedTotal++;
            }
        }

        // ② 前进 1 子格
        for (int i = 0; i < _n; i++)
        {
            int limit = i == 0 ? 0 : _d[i - 1] + MinGapSub;
            int next = _d[i] - 1;
            _d[i] = next >= limit ? next : limit;
        }

        // ③ 入口插入
        if (sourceHasItem && _n < _d.Length)
        {
            bool room = _n == 0 || _d[_n - 1] + MinGapSub <= LengthSub;
            if (room)
            {
                _d[_n] = LengthSub;
                _type[_n] = incomingType;
                _n++;
                InsertedTotal++;
            }
        }
    }

    public void Clear()
    {
        _n = 0;
        InsertedTotal = 0;
        RemovedTotal = 0;
    }
}
