using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    // ── 蓝图（复制 / 粘贴） ─────────────────────────────────────────────────

    /// <summary>
    /// 收下矩形区域里的一切。**收的是相对坐标**，所以贴到别处不带着原位置走。
    ///
    /// 取舍（两条都写在 UI 提示里，因为它们是玩家能感觉到的）：
    ///   - 机器按**左上角是否落在框内**决定收不收，不做「部分选中」的裁剪；
    ///   - 带 / 管道**只收落在框内的格子**，所以跨框的带会被截短（路径方向仍按原顺序保留）。
    /// </summary>
    private BuildResult CopyArea(GridPos a, GridPos b)
    {
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);

        _clipMachines.Clear();
        _clipBelts.Clear();
        _clipBeltLevels.Clear();
        _clipPipes.Clear();
        ClipboardOrigin = new GridPos(x0, y0);

        for (int m = 0; m < _layout.MachineSlotCount; m++)
        {
            MachineDef? def = _layout.MachineAt(m);
            if (def == null) continue;
            if (def.Pos.X < x0 || def.Pos.X > x1 || def.Pos.Y < y0 || def.Pos.Y > y1) continue;
            _clipMachines.Add(new BlueprintMachine(new GridPos(def.Pos.X - x0, def.Pos.Y - y0), def));
        }

        for (int bi = 0; bi < _layout.BeltSlotCount; bi++)
        {
            BeltDef? def = _layout.BeltAt(bi);
            if (def == null) continue;
            List<GridPos> kept = new();
            foreach (GridPos c in def.Cells)
                if (c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1)
                    kept.Add(new GridPos(c.X - x0, c.Y - y0));
            if (kept.Count > 0)
            {
                _clipBelts.Add(kept.ToArray());
                _clipBeltLevels.Add(def.Level);     // 层跟着带一起走，粘贴出来才还是高低轨
            }
        }

        for (int pi = 0; pi < _layout.PipeSlotCount; pi++)
        {
            FluidPipeDef? def = _layout.PipeAt(pi);
            if (def == null) continue;
            List<GridPos> kept = new();
            foreach (GridPos c in def.Cells)
                if (c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1)
                    kept.Add(new GridPos(c.X - x0, c.Y - y0));
            if (kept.Count > 0) _clipPipes.Add(kept.ToArray());
        }

        return new BuildResult(BuildOutcome.Placed, new GridPos(x0, y0),
            $"复制了 {_clipMachines.Count} 机器 / {_clipBelts.Count} 带 / {_clipPipes.Count} 管道（{x1 - x0 + 1}×{y1 - y0 + 1}）");
    }

    /// <summary>
    /// 粘贴。被占的格子**跳过并计数**，不整体失败 —— 用蓝图铺一片时总会有几格压着，
    /// 整体拒绝会让这个功能没法用。返回值里带着「跳过几格」，玩家一眼能看到发生了什么。
    /// </summary>
    private BuildResult Paste(GridPos origin, bool withBelts)
    {
        if (_clipMachines.Count == 0 && _clipBelts.Count == 0 && _clipPipes.Count == 0)
            return new BuildResult(BuildOutcome.NothingThere, origin, "剪贴板是空的（先框选并复制）");

        int placedM = 0, placedB = 0, placedP = 0, skipped = 0;
        int net = _layout.NetworkCount;      // 粘贴出来的设备自成一个新电网

        foreach (BlueprintMachine bm in _clipMachines)
        {
            var p = new GridPos(origin.X + bm.Rel.X, origin.Y + bm.Rel.Y);
            MachineDef src = bm.Def;
            if (!_occupancy.CanPlaceMachine(p, src.SizeX, src.SizeY)) { skipped++; continue; }
            MachineDef copy = src.MovedTo(p, src.Facing, net);
            Occupy(copy, _layout.AddMachine(copy));
            placedM++;
        }

        if (withBelts)
        {
            for (int bi = 0; bi < _clipBelts.Count; bi++)
            {
                GridPos[] rel = _clipBelts[bi];
                int level = ClipboardBeltLevel(bi);
                var cells = new GridPos[rel.Length];
                bool ok = true;
                for (int i = 0; i < rel.Length; i++)
                {
                    var p = new GridPos(origin.X + rel[i].X, origin.Y + rel[i].Y);
                    if (!_occupancy.CanPlaceBelt(p, level)) { ok = false; break; }
                    cells[i] = p;
                }
                if (!ok) { skipped++; continue; }
                var nb = new BeltDef { Cells = cells, Level = level };
                Occupy(nb, _layout.AddBelt(nb));
                placedB++;
            }
        }

        foreach (GridPos[] rel in _clipPipes)
        {
            var cells = new GridPos[rel.Length];
            bool ok = true;
            for (int i = 0; i < rel.Length; i++)
            {
                var p = new GridPos(origin.X + rel[i].X, origin.Y + rel[i].Y);
                if (!_occupancy.CanPlacePipe(p)) { ok = false; break; }
                cells[i] = p;
            }
            if (!ok) { skipped++; continue; }
            var np = new FluidPipeDef { Cells = cells };
            Occupy(np, _layout.AddPipe(np));
            placedP++;
        }

        if (placedM + placedB + placedP == 0)
            return new BuildResult(BuildOutcome.Occupied, origin, $"粘贴失败：{skipped} 项全都压着已有东西");

        return new BuildResult(BuildOutcome.Placed, origin,
            $"粘贴了 {placedM} 机器 / {placedB} 带 / {placedP} 管道，跳过 {skipped} 项");
    }

}
