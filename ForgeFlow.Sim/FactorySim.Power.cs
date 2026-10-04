using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    /// <summary>重建电网表，并按当前存活的 Working 机器全量重算需求（结构变更后调一次即可）。</summary>
    private void RebuildPower(int powerPercent)
    {
        _netCount = _layout.NetworkCount;
        _netGen = new int[_netCount];
        _networkDemand = new int[_netCount];
        _networkSat = new int[_netCount];

        for (int c = 0; c < _netCount; c++)
        {
            _netGen[c] = (int)((long)_demandPerMachine * _layout.MachinesPerNetwork[c] * powerPercent / 100);
            _networkSat[c] = SatFull;
        }

        for (int m = 0; m < _machineSlots; m++)
        {
            if (!_machineAlive[m]) continue;
            if ((MachineState)_state[m] == MachineState.Working)
                _networkDemand[_networkOf[m]] += _demandPerMachine;
        }
    }

    /// <summary>不维护电网需求的裸状态设置（只在 SyncTopology 里用，之后会全量重建）。</summary>
    private void SetStateRaw(int m, MachineState next)
    {
        _state[m] = (byte)next;
        if (next == MachineState.Working) _progress[m] = _work[m] * SatFull;
    }

    /// <summary>
    /// 电力求解。增量模式只遍历电网；非增量模式每 tick 重新聚合全部机器需求（对照）。
    /// 星形拓扑 ⇒ 一轮即精确解：sat = min(1, 出力 / 需求)，按千分比整数算，保持确定性。
    /// </summary>
    private void SolvePower()
    {
        if (!PowerEnabled)
        {
            for (int c = 0; c < _netCount; c++) _networkSat[c] = SatFull;
            return;
        }

        if (!PowerIncremental)
        {
            Array.Clear(_networkDemand, 0, _netCount);
            for (int m = 0; m < _machineSlots; m++)
            {
                if (_machineAlive[m] && (MachineState)_state[m] == MachineState.Working)
                    _networkDemand[_networkOf[m]] += _demandPerMachine;
            }
        }

        for (int c = 0; c < _netCount; c++)
        {
            int d = _networkDemand[c];
            _networkSat[c] = d <= 0
                ? SatFull
                : (int)Math.Min(SatFull, (long)_netGen[c] * SatFull / d);
        }
    }

}
