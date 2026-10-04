namespace ForgeFlow.Sim.Data;

/// <summary>
/// **机器名表** —— 由模拟线程发布、渲染线程读取（与 <see cref="ViewportCull"/> 方向相反的一对）。
///
/// 为什么要有它：
///   ① 渲染线程**不能**去读 <c>FactorySim._layout</c>（那是模拟线程的数据，直接读就是数据竞争）；
///   ② 机器名是**内容表**的信息（<c>SpriteCell → Name</c>），而内容表归模拟侧所有。
///   ⇒ 让**模拟线程在结构变更时**（<c>SyncTopology</c>，稀有事件）把「位置 + 名字」算好，
///     锁保护下发布一份**不可变数组**；渲染线程只读这份副本。零竞争、零每帧成本。
///
/// 「作为机器自身的方法」在实现上就是这个意思：名字由**机器自己的定义**推出来，
/// 任何场景都能直接拿到，不需要各场景自己维护一份表。
/// </summary>
public sealed class MachineLabelBoard
{
    /// <summary>一台机器在渲染线程看来需要的全部信息：世界坐标 + 显示名。</summary>
    public readonly record struct Entry(float X, float Y, string Name);

    private readonly object _gate = new();
    private Entry[] _items = Array.Empty<Entry>();

    /// <summary>模拟线程：发布一份新的机器名表（数组发布后**不再改动**）。</summary>
    public void Publish(Entry[] items)
    {
        lock (_gate) _items = items;
    }

    /// <summary>
    /// 渲染线程：取当前这份表。
    /// 返回的是**不可变数组的引用**，所以调用方不需要复制，也不该改它。
    /// </summary>
    public Entry[] Snapshot()
    {
        lock (_gate) return _items;
    }
}
