namespace ForgeFlow.Sim;

/// <summary>
/// 一种物品。
///
/// <see cref="Id"/> **从 1 开始，0 保留为「空」** —— 这样 `default`（全 0）天然是无效值，
/// 机器输入缓冲与快照里的物品编号都不需要额外的哨兵。
/// </summary>
public sealed record ItemDef(int Id, string Name, int IconCell);

/// <summary>
/// 一条配方：**多入多出**，一件工件（一个加工周期）。
///
/// 输入/输出都是**物品 id 的一元列表**（同一个 id 出现两次 = 需要两件）。
/// 之所以用「列表长度」而不是「id + 数量对」：这套模型里一件工件最多消耗
/// <see cref="ContentDb.MaxInputsPerRecipe"/> 件、产出同样多件，用列表就够了，
/// 而且「按需求找一个槽位」的匹配逻辑写起来最短（见 <c>FactorySim.ConsumeRecipeInputs</c>）。
/// </summary>
public sealed record RecipeDef(
    string Id,
    string Name,
    int[] Inputs,
    int[] Outputs,
    int WorkTicks,
    int FluidPerItem = 0);

/// <summary>
/// 机器美术：**图集里的内容包围盒**（不是「一格」）。
///
/// 为什么要带实测内容框而不是「一格 = 一张瓦片」：
/// 这套 sierrassets 美术里机器内容**不在单元格中心**，而且各类机器大小差很多
/// （18×51 .. 47×31）。逐精灵记下**实测的内容框**，由
/// <see cref="FactorySim.BuildSpriteLut"/> / <see cref="FactorySim.BuildSpriteSizeLut"/>
/// 换算成「按轴取样缩放 + 世界尺寸」，每台机器就能**正好落在一格内**：
/// 不跨格、不串邻格、不被裁。
///
/// ⚠ 这里的数字必须由 <c>tools/measure_art.py</c> **量**出来，不要手敲。
///   按 `srcY = row*32 - 3.5` 之类手敲出来的值非方形精灵全错：
///   LUT 取样框取「边长 = max(w,h) 的正方形」却**锚在左上角** ⇒
///   内容偏出中心、右边还把邻格的像素一起裁进来（症状：机械臂上出现邻格的图）。
///
/// ⚠ 动画帧：<paramref name="FrameStride"/> 是「帧之间隔几行」，用来推cell：
///   <c>Cell + f * FrameStride * 列数</c>。而**每帧的 Y 原点是显式给的**
///   （<paramref name="FrameYs"/>）—— 因为这套图集的行距并不严格等于 stride×32
///   （实测帧内容顶边在 482/546/609/674，间距 64/63/65），用均匀步长会裁掉/多取 1 行。
/// </summary>
public readonly record struct MachineArt(
    int Cell,
    float SrcX,
    float SrcY,
    float SrcW,
    float SrcH,
    int FrameStride = 0,
    float[]? FrameYs = null)
{
    /// <summary>帧数：显式给了每帧 Y 就以它为准，否则按 stride 推（stride ≤ 0 = 静态单帧）。</summary>
    public int FrameCount => FrameYs is { Length: > 0 }
        ? FrameYs.Length
        : FrameStride > 0 ? FactorySim.MaxMachineFrames : 1;

    /// <summary>第 f 帧的取样框（左上角 + 宽高）。</summary>
    public (float X, float Y, float W, float H) FrameRect(int frame)
    {
        if (FrameYs is { Length: > 0 } ys && (uint)frame < (uint)ys.Length)
            return (SrcX, ys[frame], SrcW, SrcH);
        return (SrcX, SrcY + frame * FrameStride * 32f, SrcW, SrcH);
    }

    /// <summary>按「行、列 + 实测内容框」建一项。</summary>
    public static MachineArt At(int row, int col, float srcX, float srcY, float srcW, float srcH,
                                int frameStride = 0, float[]? frameYs = null)
        => new(row * FactorySim.AtlasCols + col, srcX, srcY, srcW, srcH, frameStride, frameYs);

    /// <summary>
    /// 兜底美术：**行 15 / 列 1 的那台蓝色机器**（实测内容 x28..66, y482..511，即 39×30）。
    /// 探针与演示布局用的「临时可建造项」走这里。
    /// </summary>
    public static MachineArt Default => At(15, 1, 28f, 482f, 39f, 30f);
}

/// <summary>
/// **可建造项** —— 工具栏里的一行，也就是玩家能放下去的东西。
///
/// 它是「机器种类 + 配方 + 占用 + 美术」的绑定：
/// 加一种新的生产环节 = 往 <see cref="ContentDb.Buildables"/> 里加一行，**不需要改代码**。
/// 这也解决了「一台机器该做什么」的选择问题 —— 不做「先放机器再选配方」，
/// 而是**一行就是一个带配方的机器**（铁熔炉 / 铜熔炉 是两行），交互上少一层。
/// </summary>
public sealed class BuildableDef
{
    public required string Id { get; init; }

    /// <summary>工具栏与幽灵预览上显示的名字。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// **这台机器是干什么的**（菜单里显示给玩家看的一行说明）。
    ///
    /// 为什么要有它：菜单光有名字（「分流器」「均压泵」）玩家猜不出作用，
    /// 而**带配方的机器根本不需要写** —— 作用可以从配方推出来（见 <see cref="ContentDb.FunctionOf"/>）。
    /// 只有无配方的那些（出货机 / 分流器 / 合流器 / 仓储箱 / 机械臂 / 泵 / 水塔）才填这里，
    /// 于是「加一种机器 = 改内容表一行」这条性质保持不变。
    /// </summary>
    public string Note { get; init; } = "";

    public required MachineKind Kind { get; init; }

    /// <summary>美术。默认给经典蓝机器，探针的临时可建造项不必显式填。</summary>
    public MachineArt Art { get; init; } = MachineArt.Default;

    public int SizeX { get; init; } = 1;
    public int SizeY { get; init; } = 1;

    /// <summary>一件工件的 tick 数（配方机以配方为准；这里给非配方机器用）。</summary>
    public int WorkTicks { get; init; } = 60;

    /// <summary><see cref="ContentDb.Recipes"/> 的下标；-1 = 无配方（走「透传」老路径）。</summary>
    public int RecipeIndex { get; init; } = -1;

    /// <summary>仓储箱的槽位数（其它机器忽略）。</summary>
    public int StorageCapacity { get; init; }

    /// <summary>流体源 / 泵的速率（单位/tick）。</summary>
    public int FluidRate { get; init; }

    /// <summary>每件消耗的流体量（0 = 不需要流体）。</summary>
    public int FluidPerItem { get; init; }

    /// <summary>无配方生产机的初始携带物（配方机忽略，产出由配方决定）。</summary>
    public ushort InitialItemType { get; init; } = 1;

    /// <summary>
    /// 现造一个**不在内容表里**的可建造项 —— 给探针与演示布局用。
    /// <see cref="RecipeIndex"/> 保持 -1，所以行为与引入配方之前**完全一致**，
    /// 既有探针的数值因此仍是有效的回归锚点。
    /// </summary>
    public static BuildableDef AdHoc(MachineKind kind, int workTicks, int sizeX = 1, int sizeY = 1)
        => new()
        {
            Id = $"adhoc-{kind}",
            Name = kind.ToString(),
            Kind = kind,
            WorkTicks = workTicks,
            SizeX = sizeX,
            SizeY = sizeY,
        };

    /// <summary>
    /// 按这份定义造一台机器（稳定 id 由布局分配）。
    ///
    /// ⚠ **有配方时，加工时长与每件耗水量以配方为准**，本定义里那两个字段被忽略 ——
    ///   否则同一个数字会有两份真相，改一处漏一处就是「配方说 60 tick、机器却按 40 走」。
    ///   无配方的机器（出货机 / 分流器 / 合流器 / 机械臂 / 仓储箱…）才用自己的 WorkTicks。
    /// </summary>
    public MachineDef ToMachineDef(ContentDb db, GridPos pos, int facing, int networkId, ushort initialItemType)
    {
        RecipeDef? recipe = db.RecipeOf(RecipeIndex);
        return new MachineDef
        {
            Kind = Kind,
            Pos = pos,
            WorkTicks = recipe?.WorkTicks ?? WorkTicks,
            NetworkId = networkId,
            SizeX = SizeX,
            SizeY = SizeY,
            SpriteCell = Art.Cell,
            FrameStride = Art.FrameStride,
            FrameCount = Art.FrameCount,
            RecipeIndex = RecipeIndex,
            StorageCapacity = StorageCapacity,
            FluidRate = FluidRate,
            FluidPerItem = recipe?.FluidPerItem ?? FluidPerItem,
            Facing = facing,
            InitialItemType = initialItemType,
        };
    }
}

/// <summary>
/// **内容表** —— 物品 / 配方 / 可建造项，全部是数据。
///
/// 「机器该产出什么、一台机器长什么样、加工要多久」**只在这张表里**，
/// **不要**散落到 <c>FactorySim</c> 的常量与 <c>switch</c> 里 —— 那样加一种环节就要改代码。
/// 这张表是唯一的真相来源，模拟只按 <c>RecipeIndex</c> 查表。
///
/// ⚠ 物品图标的取法见 <see cref="FactorySim.BuildSpriteLut"/>：
/// 这套美术**把图标画在各自 32×32 单元格的左上角**（约 12×11），
/// 所以图标编号只能从 <see cref="FactorySim.ItemIconCells"/> 里挑，不能随便指。
/// </summary>
public sealed class ContentDb
{
    /// <summary>一条配方最多几个输入 / 输出。机器输入缓冲的宽度就是按它定的。</summary>
    public const int MaxInputsPerRecipe = 8;

    public IReadOnlyList<ItemDef> Items { get; }
    public IReadOnlyList<RecipeDef> Recipes { get; }
    public IReadOnlyList<BuildableDef> Buildables { get; }

    private readonly int[] _itemIndexById;

    public ContentDb(IReadOnlyList<ItemDef> items, IReadOnlyList<RecipeDef> recipes,
                     IReadOnlyList<BuildableDef> buildables)
    {
        Items = items;
        Recipes = recipes;
        Buildables = buildables;

        int maxId = 0;
        foreach (ItemDef it in items) if (it.Id > maxId) maxId = it.Id;
        _itemIndexById = new int[maxId + 1];
        Array.Fill(_itemIndexById, -1);
        for (int i = 0; i < items.Count; i++) _itemIndexById[items[i].Id] = i;

        for (int r = 0; r < recipes.Count; r++)
        {
            if (recipes[r].Inputs.Length > MaxInputsPerRecipe || recipes[r].Outputs.Length > MaxInputsPerRecipe)
                throw new InvalidOperationException(
                    $"配方 '{recipes[r].Id}' 的输入/输出条数超过了上限 {MaxInputsPerRecipe}。");
            foreach (int id in recipes[r].Inputs) RequireKnown(id, recipes[r].Id);
            foreach (int id in recipes[r].Outputs) RequireKnown(id, recipes[r].Id);
        }

        foreach (BuildableDef b in buildables)
        {
            // -1 = 「这台机器没有配方」，是**合法值**；比 -1 更小就是表写错了。
            // ⚠ 只查上界的话，负数会被 RecipeOf 当成「没有配方」静默接受 ——
            //   而这个构造函数存在的意义就是**在表写错时当场炸掉**。
            if (b.RecipeIndex < -1 || b.RecipeIndex >= recipes.Count)
                throw new InvalidOperationException(
                    $"可建造项 '{b.Id}' 的 RecipeIndex = {b.RecipeIndex} 非法" +
                    $"（合法值：-1 = 无配方，或 [0, {recipes.Count})）。");
            if (b.StorageCapacity > FactorySim.InvSlots)
                throw new InvalidOperationException(
                    $"可建造项 '{b.Id}' 的仓储容量 {b.StorageCapacity} 超过了机器输入缓冲宽度 {FactorySim.InvSlots}。");
        }
    }

    private void RequireKnown(int itemId, string recipeId)
    {
        if (!IsKnownItem(itemId))
            throw new InvalidOperationException($"配方 '{recipeId}' 引用了未定义的物品 id={itemId}。");
    }

    public bool IsKnownItem(int itemId) => (uint)itemId < (uint)_itemIndexById.Length && _itemIndexById[itemId] >= 0;

    public ItemDef Item(int itemId)
        => IsKnownItem(itemId)
            ? Items[_itemIndexById[itemId]]
            : throw new ArgumentOutOfRangeException(nameof(itemId), $"未定义的物品 id={itemId}");

    public string ItemName(int itemId) => IsKnownItem(itemId) ? Items[_itemIndexById[itemId]].Name : $"#{itemId}";

    /// <summary>按名字找物品（探针里写断言时比记 id 数字可靠）。找不到就抛。</summary>
    public ItemDef ItemByName(string name)
    {
        foreach (ItemDef it in Items) if (it.Name == name) return it;
        throw new KeyNotFoundException($"内容表里没有叫「{name}」的物品");
    }

    /// <summary>物品图标编号（进图集的精灵 id）。</summary>
    public int ItemIcon(int itemId) => IsKnownItem(itemId) ? Items[_itemIndexById[itemId]].IconCell : 0;

    /// <summary>这台机器用的配方；无配方返回 null。</summary>
    public RecipeDef? RecipeOf(int recipeIndex)
        => (uint)recipeIndex < (uint)Recipes.Count ? Recipes[recipeIndex] : null;

    /// <summary>
    /// **这台机器是干什么的**（菜单与选中信息用的一行说明）。
    ///
    /// 规则（都在数据里，没有第二处真相）：
    ///   · 有配方 ⇒ 从配方推：「炼铁板：铁矿 → 铁板」；没有输入的（矿机）写成「采铁矿：产出 铁矿」；
    ///     要流体再补「（耗水 20/件）」。
    ///   · 无配方 ⇒ 用 <see cref="BuildableDef.Note"/>（出货机、分流器、泵…… 的作用只能写）。
    /// </summary>
    public string FunctionOf(BuildableDef b)
    {
        if (RecipeOf(b.RecipeIndex) is not { } r) return b.Note;

        string Ins() => r.Inputs.Length == 0 ? "" : string.Join("+", r.Inputs.Select(ItemName));
        string Outs() => string.Join("+", r.Outputs.Select(ItemName));
        string body = r.Inputs.Length == 0
            ? $"{r.Name}：产出 {Outs()}"
            : $"{r.Name}：{Ins()} → {Outs()}";
        return r.FluidPerItem > 0 ? $"{body}（耗水 {r.FluidPerItem}/件）" : body;
    }

    // ── 内置内容表 ──────────────────────────────────────────────────────────
    //
    // 美术来源：全部由 `python tools/measure_art.py` **量出来的内容包围盒**
    // （不是手敲的推算值 —— 手敲出来的非方形精灵全错，见 MachineArt 的注释）。
    // 对应行/列：
    //   行 15/17/19/21  蓝机器（带观察窗）—— 这套包里唯一有 4 帧动画的机器
    //   行 24/25/26/27  蓝机器（带显示屏）
    //   行 33/35/37/39  47×31 的大机器
    //   行 73          木箱
    //   行 44+45       水塔（含上方蒸汽）
    //   行 94+95       洗矿机（窄高容器）
    //   行 100+101     均压泵
    //   行 106+107     绿色长臂机械臂

    // ⚠ **图标格只能从 FactorySim.ItemIconCells 里挑**，这是硬约束，不是风格。
    //   为什么：渲染侧的 LUT 是**按 ItemIconCells 逐格填**的（见 FactorySim.FillSpriteTables）——
    //   不在这张表里的格子连 UV/尺寸都不会被填，物品退回「整格取样 + 整格尺寸」：
    //   画出来是 32×32 的**透明方块**，图标缩在左上角一小块、下半截透出带面，
    //   而且**所有物品长得一模一样**（按主色扫描换格时漏登记的格子就是它 ——
    //   铁板/铜板/齿轮画出来全是同一个方块的变体）。
    //   「主色对不对」说明的是**语义匹配**，完全不说明这一格**能不能被采样**。
    //
    //   量法：`python tools/measure_art.py sheet` —— 它按整格窗口量出内容包围盒、
    //   自检有没有溢出到邻格，并直接把 ItemIconBoxes 的 C# 行打出来（绝对图集坐标）。
    //   配法（按 12×11 的图标形状 + 主色定语义）：
    //     · 格 7  灰色六边形   → 铁矿（灰蓝色的矿块）
    //     · 格 17 #B05838 锈铜方块 → 铜矿
    //     · 格 23 钢蓝斜纹板   → 铁板
    //     · 格 22 锈铜斜纹板   → 铜板
    //     · 格 31 中间镂空     → 齿轮轮廓
    //     · 格 20 #438C5D 绿色带纹路 → 电路板
    //     · 格 27 琥珀色箱体   → 铁箱
    private static readonly ItemDef[] DefaultItems =
    {
        new(1, "铁矿", 0 * FactorySim.AtlasCols + 7),     // 灰色六边形
        new(2, "铜矿", 1 * FactorySim.AtlasCols + 7),     // 锈铜方块
        new(3, "铁板", 2 * FactorySim.AtlasCols + 3),     // 钢蓝斜纹板
        new(4, "铜板", 2 * FactorySim.AtlasCols + 2),     // 锈铜斜纹板
        new(5, "齿轮", 3 * FactorySim.AtlasCols + 1),     // 齿轮轮廓
        new(6, "电路板", 2 * FactorySim.AtlasCols + 0),   // 绿色电路板
        new(7, "铁箱", 2 * FactorySim.AtlasCols + 7),     // 琥珀箱
    };

    private static readonly RecipeDef[] DefaultRecipes =
    {
        //       id             名字        输入          输出         tick  流体
        new("mine-iron", "采铁矿", Array.Empty<int>(), new[] { 1 }, 40),
        new("mine-copper", "采铜矿", Array.Empty<int>(), new[] { 2 }, 40),
        new("smelt-iron", "炼铁板", new[] { 1 }, new[] { 3 }, 60),
        new("smelt-copper", "炼铜板", new[] { 2 }, new[] { 4 }, 60),
        new("gear", "削齿轮", new[] { 3, 3 }, new[] { 5 }, 80),
        new("circuit", "刻电路板", new[] { 3, 4 }, new[] { 6, 6 }, 100),
        new("crate", "打铁箱", new[] { 3, 3 }, new[] { 7 }, 60),
        new("wash-iron", "洗铁矿", new[] { 1 }, new[] { 3 }, 40, FluidPerItem: 20),
    };

    private static BuildableDef[] DefaultBuildables()
    {
        int R(string id) => Array.FindIndex(DefaultRecipes, r => r.Id == id);

        return new[]
        {
            new BuildableDef
            {
                Id = "iron-mine", Name = "铁矿机", Kind = MachineKind.Producer,
                RecipeIndex = R("mine-iron"), WorkTicks = 40,
                Art = MachineArt.At(24, 1, 28f, 770f, 39f, 30f),
            },
            new BuildableDef
            {
                Id = "copper-mine", Name = "铜矿机", Kind = MachineKind.Producer,
                RecipeIndex = R("mine-copper"), WorkTicks = 40,
                Art = MachineArt.At(25, 1, 28f, 802f, 39f, 30f),
            },
            new BuildableDef
            {
                Id = "iron-furnace", Name = "铁熔炉", Kind = MachineKind.Processor,
                RecipeIndex = R("smelt-iron"), WorkTicks = 60,
                Art = MachineArt.At(33, 1, 24f, 1056f, 47f, 31f),
            },
            new BuildableDef
            {
                Id = "copper-furnace", Name = "铜熔炉", Kind = MachineKind.Processor,
                RecipeIndex = R("smelt-copper"), WorkTicks = 60,
                Art = MachineArt.At(35, 1, 24f, 1120f, 47f, 31f),
            },
            new BuildableDef
            {
                Id = "gear-assembler", Name = "齿轮机", Kind = MachineKind.Processor,
                RecipeIndex = R("gear"), WorkTicks = 80,
                Art = MachineArt.At(37, 1, 24f, 1184f, 47f, 31f),
            },
            new BuildableDef
            {
                Id = "circuit-assembler", Name = "电路板机", Kind = MachineKind.Processor,
                RecipeIndex = R("circuit"), WorkTicks = 100,
                Art = MachineArt.At(39, 1, 24f, 1248f, 47f, 31f),
            },
            new BuildableDef
            {
                Id = "shipper", Name = "出货机", Kind = MachineKind.Shipper, WorkTicks = 30,
                Note = "把送来的货送出工厂（算产量）",
                Art = MachineArt.At(27, 1, 28f, 866f, 39f, 30f),
            },
            new BuildableDef
            {
                Id = "splitter", Name = "分流器", Kind = MachineKind.Splitter, WorkTicks = 10,
                Note = "1 进 2 出，轮流分配",
                // 行 15/17/19/21 是**同一台机器的 4 帧**（这套包里唯一有真动画的机器）。
                // 帧 cell 按 stride=2 行推（151/171/191/211），每帧的 Y 原点显式给
                // —— 实测内容顶边在 482/546/609/674，间距不是整齐的 64。
                // 帧由加工进度驱动，所以欠供电时画面上的运转也变慢。
                Art = MachineArt.At(15, 1, 28f, 482f, 39f, 31f, frameStride: 2,
                                    frameYs: new[] { 482f, 546f, 609f, 674f }),
            },
            new BuildableDef
            {
                Id = "merger", Name = "合流器", Kind = MachineKind.Merger, WorkTicks = 10,
                Note = "2 进 1 出，合并两条带",
                Art = MachineArt.At(26, 1, 28f, 834f, 39f, 30f),
            },
            new BuildableDef
            {
                Id = "storage", Name = "仓储箱", Kind = MachineKind.Storage,
                StorageCapacity = 12, WorkTicks = 10,
                Note = "存 12 件，先进先出",
                Art = MachineArt.At(73, 1, 28f, 2337f, 39f, 30f),
            },
            new BuildableDef
            {
                Id = "inserter", Name = "长臂机械臂", Kind = MachineKind.Inserter, WorkTicks = 20,
                Note = "跨一格搬运（够得到两格远）",
                Art = MachineArt.At(106, 0, 4f, 3415f, 27f, 41f),
            },
            new BuildableDef
            {
                Id = "pump", Name = "均压泵", Kind = MachineKind.Pump, FluidRate = 10, WorkTicks = 1,
                Note = "把水在两个相邻管网间搬平",
                Art = MachineArt.At(100, 0, 4f, 3223f, 23f, 41f),
            },
            new BuildableDef
            {
                Id = "fluid-source", Name = "水塔", Kind = MachineKind.FluidSource, FluidRate = 10, WorkTicks = 1,
                Note = "往相邻管网注水（每 tick 10）",
                Art = MachineArt.At(44, 1, 28f, 1431f, 39f, 40f),
            },
            new BuildableDef
            {
                Id = "washer", Name = "洗矿机", Kind = MachineKind.FluidUser,
                RecipeIndex = R("wash-iron"), FluidPerItem = 20,
                Art = MachineArt.At(94, 0, 4f, 3021f, 18f, 51f),
            },
        };
    }

    /// <summary>内置内容表（单例）。</summary>
    public static ContentDb Default { get; } =
        new(DefaultItems, DefaultRecipes, DefaultBuildables());

    /// <summary>按 id 找可建造项；找不到返回 null。</summary>
    public BuildableDef? Buildable(string id)
    {
        foreach (BuildableDef b in Buildables) if (b.Id == id) return b;
        return null;
    }

    /// <summary>
    /// 按**图集格**反查名字。机器定义（<c>MachineDef</c>）里只有图集格、没有名字，
    /// 所以「给机器显示名字」必须走这里 —— 这也是「机器名由**内容表**推导」的实现处：
    /// 名字是机器自身定义的一部分，任何场景都拿得到，不需要各场景自己维护一份表。
    /// </summary>
    public string NameOfSpriteCell(int cell)
    {
        foreach (BuildableDef b in Buildables) if (b.Art.Cell == cell) return b.Name;
        return "?";
    }
}
