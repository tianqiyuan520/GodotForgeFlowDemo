using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    private void TickMachine(int m)
    {
        // **接线闸门**：没接够线的机器完全不工作 —— 不产出、不取料、不推货。
        // ⚠ 必须在最前面，且**覆盖所有种类**（仓储箱走的是另一条路径，也一并挡住）。
        //   ⚠ **不要**只靠"输入带为空 ⇒ 睡眠"来体现：**出货机/合流器照样会出货**
        //   （它们在没有输入带时会走"透传"那条路）。
        if (!_wired[m]) return;

        // 仓储箱走自己的「两向流」逻辑（常驻活跃，不参与唤醒边）。理由见 SyncTopology。
        if (_layout.MachineAt(m)!.Kind == MachineKind.Storage)
        {
            TickStorage(m);
            return;
        }

        switch ((MachineState)_state[m])
        {
            case MachineState.WaitingInput:
            {
                if (_recipeOf[m] >= 0)
                {
                    TickRecipeWaiting(m);
                    return;
                }

                int[] ins = _inBelts[m];
                if (ins.Length == 0)
                {
                    // 没有输入带的两种：纯生产机（不该走这条路径）与**要流体但不吃固体料的机器**。
                    // 后者靠管网供料：取得到就开始，取不到就睡，等 W3。
                    if (_fluidPerItem[m] > 0)
                    {
                        if (TryDrawFluid(m))
                        {
                            SetState(m, MachineState.Working);
                            _active[m] = true;
                            return;
                        }
                        _active[m] = false;         // 等 W3（管网来流体）
                    }
                    return;
                }
                // 合流器就是走这个循环：**按带 id 升序**取第一条有货的 ⇒ 确定性
                for (int k = 0; k < ins.Length; k++)
                {
                    if (_belts[ins[k]]!.TryRemoveAtExit(out ItemSlot item))
                    {
                        _carried[m] = item.Type;
                        SetState(m, MachineState.Working);
                        _active[m] = true;
                        return;
                    }
                }
                _active[m] = false;                             // 全部没料 → 睡眠，等 W1
                return;
            }

            case MachineState.Working:
                // 进度单位是「千分比·tick」：满供电 1000 ⇒ 每 tick 减 1000，workTicks 个 tick 完成。
                // 半供电 500 ⇒ 时间翻倍。整数运算，跨机器确定。
                _progress[m] -= SatOf(m);
                if (_progress[m] > 0) return;
                FinishWork(m);
                return;

            case MachineState.BlockedOutput:
                FinishWork(m);
                return;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 配方加工 —— 引入它之前「加工机」其实不做加工：它把物品原样透传，
    // 所以分流器 / 合流器 / 流体这些基础设施虽然都对，却**没有主角**。
    // 现在机器按 <see cref="RecipeDef"/> 收多路输入、出多路产出。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 配方机在 <see cref="MachineState.WaitingInput"/> 的一个 tick：
    /// 抓还缺的料 → 齐了就扣流体、消费输入、开工；otherwise 睡下等 W1 / W3。
    /// </summary>
    private void TickRecipeWaiting(int m)
    {
        RecipeDef? recipe = _content.RecipeOf(_recipeOf[m]);
        if (recipe == null) { _active[m] = false; return; }

        int baseIdx = m * InvSlots;

        // ① 抓料：**只抓这条配方还缺的**。
        //    不这么做的后果是机器把带上的杂料也吞进来，然后被自己吃下的垃圾堵死 ——
        //    而那是「静默卡死」最难查的一类。
        int[] ins = _inBelts[m];
        for (int k = 0; k < ins.Length; k++)
        {
            GapLine belt = _belts[ins[k]]!;
            while (belt.Count > 0 && belt.GapAt(0) == 0)
            {
                ushort type = belt.TypeOf(0);
                int slot = WantedFreeSlot(baseIdx, recipe, type);
                if (slot < 0) break;                             // 这个种类够了 / 没空位
                if (!belt.TryRemoveAtExit(out ItemSlot item)) break;
                _inv[baseIdx + slot] = item.Type;
            }
        }

        // ② 凑齐了没（合流器喂多输入配方靠的就是这一步跨 tick 累积）
        if (!RecipeReady(baseIdx, recipe)) { _active[m] = false; return; }

        // ③ 流体：需要就扣；扣不到就睡下等 W3
        if (_fluidPerItem[m] > 0 && !TryDrawFluid(m)) { _active[m] = false; return; }

        // ④ 消费输入、开工
        ConsumeInputs(baseIdx, recipe);
        _outPushed[m] = 0;
        _craftsStarted[m]++;
        SetState(m, MachineState.Working);
        _active[m] = true;
    }

    /// <summary>这条配方要几个 <paramref name="type"/>（同一个 id 在输入表里出现两次就是两个）。</summary>
    private static int RequiredCount(RecipeDef r, ushort type)
    {
        int n = 0;
        foreach (int id in r.Inputs) if (id == type) n++;
        return n;
    }

    /// <summary>还可以再收一个 <paramref name="type"/> 的空槽位；不收 / 没空位返回 -1。</summary>
    private int WantedFreeSlot(int baseIdx, RecipeDef r, ushort type)
    {
        int want = RequiredCount(r, type);
        if (want == 0) return -1;

        int have = 0;
        for (int k = 0; k < InvSlots; k++) if (_inv[baseIdx + k] == type) have++;
        if (have >= want) return -1;

        for (int k = 0; k < InvSlots; k++) if (_inv[baseIdx + k] == 0) return k;
        return -1;
    }

    /// <summary>输入是否已凑齐一件。需求按顺序逐个找一个**独占**的匹配槽位。</summary>
    private bool RecipeReady(int baseIdx, RecipeDef r)
    {
        Array.Clear(_recipeScratch, 0, InvSlots);
        foreach (int need in r.Inputs)
        {
            bool found = false;
            for (int k = 0; k < InvSlots; k++)
            {
                if (_recipeScratch[k] || _inv[baseIdx + k] != need) continue;
                _recipeScratch[k] = true;
                found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>
    /// 扣掉一件的输入。
    ///
    /// ⚠ **必须与 <see cref="RecipeReady"/> 用同一套匹配顺序**（需求顺序 × 槽位升序）。
    ///   两边顺序不一致时，「判定齐了」与「扣掉哪几个」会指向不同的槽位 ——
    ///   表现是偶尔丢料 / 凭空多料，而且只在同一物品占多个槽位时才出现。
    /// </summary>
    private void ConsumeInputs(int baseIdx, RecipeDef r)
    {
        Array.Clear(_recipeScratch, 0, InvSlots);
        foreach (int need in r.Inputs)
        {
            for (int k = 0; k < InvSlots; k++)
            {
                if (_recipeScratch[k] || _inv[baseIdx + k] != need) continue;
                _recipeScratch[k] = true;
                _inv[baseIdx + k] = 0;
                break;
            }
        }
    }

    /// <summary>
    /// 配方机做完一件：把产出**接着上次的断点**推进输出带。
    ///
    /// <c>_outPushed</c> 就是断点：多产出配方推出第 1 件、第 2 件被堵住时，
    /// 重试必须接着第 2 件推 —— 从头再来会**凭空多产一件**。
    /// </summary>
    private void FinishRecipeWork(int m)
    {
        RecipeDef? recipe = _content.RecipeOf(_recipeOf[m]);
        if (recipe == null) { _active[m] = false; return; }

        int[] outs = _outBelts[m];
        for (int i = _outPushed[m]; i < recipe.Outputs.Length; i++)
        {
            if (outs.Length == 0)
            {
                ShippedTotal++;              // 没有输出带 ⇒ 产出一律计入出货
            }
            else if (!TryPushToOutputs(m, outs, (ushort)recipe.Outputs[i]))
            {
                SetState(m, MachineState.BlockedOutput);
                _active[m] = false;                            // 等 W2
                return;
            }
            _outPushed[m] = i + 1;
        }

        _outPushed[m] = 0;
        _craftsFinished[m]++;      // 产出全部推出去了，才算「完工」——守恒等式用这个数算产出
        SetState(m, MachineState.WaitingInput);
        // 保持活跃一个 tick：带上可能还有料，立刻接手下一件。
        // 凑不齐时 TickRecipeWaiting 会把自己睡下，所以这里不会漏掉唤醒。
        _active[m] = true;
    }

    // ── 仓储箱 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 仓储箱：**先出后进**，一次一件，先进先出。
    ///
    /// 顺序固定为「先出后进」是为了确定性：同一 tick 里既进又出时，
    /// 先出能让下游优先拿到料，也让「箱子满了以后上游还能不能进」有确定答案。
    /// </summary>
    private void TickStorage(int m)
    {
        int baseIdx = m * InvSlots;
        int cap = _storageSlots[m];
        if (cap <= 0) return;

        // ① 出：队首那件推到输出带（推不进去就留着，下次再说）
        int[] outs = _outBelts[m];
        if (outs.Length > 0)
        {
            int head = -1;
            for (int k = 0; k < cap; k++) if (_inv[baseIdx + k] != 0) { head = k; break; }
            if (head >= 0 && TryPushToOutputs(m, outs, _inv[baseIdx + head]))
            {
                _inv[baseIdx + head] = 0;
                for (int k = head; k + 1 < cap; k++) _inv[baseIdx + k] = _inv[baseIdx + k + 1];
                _inv[baseIdx + cap - 1] = 0;
            }
        }

        // ② 进：从输入带收（**不限种类** —— 箱子就是个缓冲，不该挑食）
        int[] ins = _inBelts[m];
        for (int k = 0; k < ins.Length; k++)
        {
            GapLine belt = _belts[ins[k]]!;
            while (belt.Count > 0 && belt.GapAt(0) == 0)
            {
                int slot = -1;
                for (int i = 0; i < cap; i++) if (_inv[baseIdx + i] == 0) { slot = i; break; }
                if (slot < 0) break;                             // 满了
                if (!belt.TryRemoveAtExit(out ItemSlot item)) break;
                _inv[baseIdx + slot] = item.Type;
            }
        }
    }

    /// <summary>加工完成 / 被唤醒后，把产出推进输出带。推不进去就转 BlockedOutput 睡觉。</summary>
    private void FinishWork(int m)
    {
        if (_recipeOf[m] >= 0) { FinishRecipeWork(m); return; }

        int[] outs = _outBelts[m];
        if (outs.Length > 0)
        {
            if (!TryPushToOutputs(m, outs))
            {
                SetState(m, MachineState.BlockedOutput);
                _active[m] = false;                             // 等 W2（输出带入口腾出空间）
                return;
            }
        }
        else
        {
            ShippedTotal++;
        }

        if (_inBelts[m].Length == 0 && _layout.MachineAt(m)!.Kind == MachineKind.Producer)
        {
            // ✅ 透传生产机没有输入带 ⇒ W1 永远不会为它触发，
            // 所以它**绝不能睡眠**，必须自己立刻开始下一件。
            // 首版就是这里写成了无条件 _active[m] = false，导致生产机产出一件后
            // 永久睡眠（症状：活跃占比 0.5%、带上一件物品都没有），
            // 被探针 03 的等价比对在 tick 31 抓到。
            _carried[m] = (ushort)(_carried[m] % _typeCount + 1);
            SetState(m, MachineState.Working);
            _active[m] = true;
            return;
        }

        SetState(m, MachineState.WaitingInput);
        // 要流体的机器也走这条路径，但它没有 W1 可等 —— 让它多 poll 一个 tick，
        // 取得到就接着干；取不到时 WaitingInput 分支会把 _active 置回 false 并等 W3。
        //
        // ⚠ 不能只靠 W3：管网**满**的时候源不会再注入，也就不会触发 W3。
        //   （满 ⇒ 存量 ≥ 需求 ⇒ 这一 tick 的 poll 必然成功，所以不会死锁。）
        // ⚠ 判据是 FluidPerItem > 0，不是 Kind —— 配方也可以要求流体（见 RebuildFluid）。
        _active[m] = _fluidPerItem[m] > 0;
    }

    /// <summary>
    /// 把携带物推进某一条输出带；推得进去返回 true。
    ///
    /// **分流器的轮转就在这里**：从游标位置开始依次试，第一条放得下的就用，
    /// 并把游标移到它后面一条 ⇒ 下一个物品走另一条。
    /// 全部放不下 ⇒ BlockedOutput，等 W2（任意一条输出带腾出空间）唤醒 ——
    /// 这也正是「分流器被一条堵住的输出带卡住」时该有的行为：它不该死等那一条。
    ///
    /// 一进一出（outs.Length == 1）时这段退化成「只有一条，试它」，
    /// 游标恒为 0，行为与引入分流器之前完全一致。
    /// </summary>
    private bool TryPushToOutputs(int m, int[] outs) => TryPushToOutputs(m, outs, _carried[m]);

    /// <summary>指定要推出去的物品（配方机与仓储箱用；分流器的轮转语义对二者一样适用）。</summary>
    private bool TryPushToOutputs(int m, int[] outs, ushort type)
    {
        int n = outs.Length;
        int start = _splitCursor[m];
        for (int k = 0; k < n; k++)
        {
            int idx = (start + k) % n;
            if (_belts[outs[idx]]!.TryInsert(type))
            {
                _splitCursor[m] = (idx + 1) % n;
                return true;
            }
        }
        return false;
    }

}
