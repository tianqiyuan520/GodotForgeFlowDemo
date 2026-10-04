using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    /// <summary>
    /// 重建流体表。结构变更后调一次（与电网同理：结构变更是玩家操作级别的事件）。
    ///
    /// ⚠ **必须保留已存的流体**：管网里的存量不能因为旁边加了一台机器就蒸发 ——
    /// 否则玩家每放一台机器就把管网清空一次，"边建边跑"直接不成立。
    ///
    /// ⚠⚠ **而且不能凭空产生到别的管网里去。** **不要**「按容量比例分给**所有**新网络」：
    ///   总量守恒，但**分布是编的** —— 只要结构变更（放一台机器、接一条线、拆一段管都算）
    ///   就会把 A 网的水按容量比例洒进一个跟它毫无关系的 B 网。
    ///   实测（探针 10「均压泵」的反例）：反例场景本不该有一滴水，因为多做了一次
    ///   `SyncTopology`，B 网里出现了 **5** 单位 ⇒ 反例判据当场报「流体凭空跨网」。
    ///   ⇒ 存量必须**按格子的归属搬**：每个旧网络的水按格子数摊到它自己的格子上，
    ///   再把每格的水搬到"这格现在属于哪个新网" —— 拆分会跟着格子走、合并会相加，
    ///   总量精确守恒，且**绝不流进与它无关的网**。
    /// </summary>
    private void RebuildFluid()
    {
        int nets = _layout.FluidNetworkCount;
        var capacity = new int[nets];
        for (int n = 0; n < nets; n++) capacity[n] = _layout.FluidCellsOf(n) * FluidPerCell;

        // 旧网络的水 → 摊到它当时的每一格上（余数给靠前的格，纯整数 ⇒ 确定）
        var volume = new int[nets];
        for (int old = 0; old < _fluidCellsOfNet.Length; old++)
        {
            int held = (uint)old < (uint)_fluidVolume.Length ? _fluidVolume[old] : 0;
            List<GridPos> cells = _fluidCellsOfNet[old];
            if (held <= 0 || cells == null || cells.Count == 0) continue;

            int per = held / cells.Count;
            int rest = held - per * cells.Count;
            for (int i = 0; i < cells.Count; i++)
            {
                int now = _layout.FluidNetworkAt(cells[i]);
                if ((uint)now >= (uint)nets) continue;          // 那格已经不是管道了（被拆掉）
                volume[now] += per + (i < rest ? 1 : 0);
            }
        }

        // 容量可能因为拆管子变小 ⇒ 夹回上限（多出来的那点是真的没地方放了）
        for (int n = 0; n < nets; n++)
            if (volume[n] > capacity[n]) volume[n] = capacity[n];

        _fluidCapacity = capacity;
        _fluidVolume = volume;

        // 刷新「网络 → 格子」快照，供**下一次**结构变更时按格搬家
        var byNet = new List<GridPos>[nets];
        for (int i = 0; i < _layout.PipeSlotCount; i++)
        {
            FluidPipeDef? pipe = _layout.PipeAt(i);
            if (pipe == null) continue;
            foreach (GridPos c in pipe.Cells)
            {
                int n = _layout.FluidNetworkAt(c);
                if ((uint)n >= (uint)nets) continue;
                (byNet[n] ??= new List<GridPos>()).Add(c);
            }
        }
        _fluidCellsOfNet = byNet;
        _fluidNetworkOf = new int[_machineSlots];
        _fluidNetworkB = new int[_machineSlots];
        _fluidRate = new int[_machineSlots];
        _fluidPerItem = new int[_machineSlots];

        for (int m = 0; m < _machineSlots; m++)
        {
            MachineDef? def = _layout.MachineAt(m);
            if (def == null) continue;
            _fluidNetworkOf[m] = def.FluidNetwork;
            _fluidNetworkB[m] = def.FluidNetworkB;
            _fluidRate[m] = def.FluidRate;
            _fluidPerItem[m] = def.FluidPerItem;
        }

        // 每个管网上「等流体」的机器清单（按 id 升序 ⇒ 唤醒顺序确定）。
        //
        // ⚠ 判据是 **FluidPerItem > 0**，不是 Kind == FluidUser。
        //   配方可以要求流体（一台加工机也可能要水），用 Kind 判定的话那类机器
        //   根本不在清单里 ⇒ 永远等不到 W3 ⇒ 静默卡死。这是引入配方时新出现的一类坑。
        var waiters = new List<int>[nets];
        for (int m = 0; m < _machineSlots; m++)
        {
            if (!_machineAlive[m]) continue;
            if (_fluidPerItem[m] <= 0) continue;
            int net = _fluidNetworkOf[m];
            if (net < 0) continue;
            (waiters[net] ??= new List<int>()).Add(m);
        }
        _fluidWaitersByNet = new int[nets][];
        for (int n = 0; n < nets; n++)
            _fluidWaitersByNet[n] = waiters[n]?.ToArray() ?? Array.Empty<int>();
    }

    /// <summary>
    /// 流体每 tick 一次。顺序固定：**先源（id 升序）注入 → 再泵搬运 → 再唤醒等流体的机器**。
    /// 用户的消耗发生在它自己的状态机里（见 <see cref="TryDrawFluid"/>）。
    /// </summary>
    private void SolveFluid()
    {
        if (_fluidCapacity.Length == 0) return;

        for (int m = 0; m < _machineSlots; m++)
        {
            if (!_machineAlive[m]) continue;
            if (_layout.MachineAt(m)!.Kind != MachineKind.FluidSource) continue;

            int net = _fluidNetworkOf[m];
            if (net < 0) continue;

            int space = _fluidCapacity[net] - _fluidVolume[net];
            if (space <= 0) continue;

            int add = Math.Min(_fluidRate[m], space);
            if (add <= 0) continue;

            _fluidVolume[net] += add;

            // **W3**：管网有流体了 ⇒ 唤醒这个网上等流体的机器。
            // 前两条唤醒边（W1/W2）是给带的，这条是给管网的 —— 漏掉它就是「机器永远等不到流体」。
            WakeFluidWaiters(net);
        }

        // 泵：在两个相邻管网之间搬流体（id 升序 ⇒ 多台泵的搬运顺序确定）。
        // 方向规则：**从存量多的搬到存量少的**；相等时从编号小的搬到编号大的。
        // 这条规则是确定的（纯整数比较），而且与「水位会自己找平」的直觉一致。
        for (int m = 0; m < _machineSlots; m++)
        {
            if (!_machineAlive[m]) continue;
            if (_layout.MachineAt(m)!.Kind != MachineKind.Pump) continue;

            int a = _fluidNetworkOf[m], b = _fluidNetworkB[m];
            if (a < 0 || b < 0 || a == b) continue;

            int src = a, dst = b;
            if (_fluidVolume[b] > _fluidVolume[a]) { src = b; dst = a; }

            int move = _fluidRate[m];
            int space = _fluidCapacity[dst] - _fluidVolume[dst];
            if (move > space) move = space;
            if (move > _fluidVolume[src]) move = _fluidVolume[src];
            if (move <= 0) continue;

            _fluidVolume[src] -= move;
            _fluidVolume[dst] += move;
            WakeFluidWaiters(dst);
        }
    }

    private void WakeFluidWaiters(int net)
    {
        foreach (int u in _fluidWaitersByNet[net])
        {
            if (!_active[u] && (MachineState)_state[u] == MachineState.WaitingInput)
                _active[u] = true;
        }
    }

    /// <summary>机器从它所属管网取够一件所需的流体；取得到返回 true（并扣掉）。</summary>
    private bool TryDrawFluid(int m)
    {
        int net = _fluidNetworkOf[m];
        if (net < 0) return false;

        int need = _fluidPerItem[m];
        if (need > 0 && _fluidVolume[net] < need) return false;

        _fluidVolume[net] -= need;
        return true;
    }

}
