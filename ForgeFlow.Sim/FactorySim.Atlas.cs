using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeFlow.Sim.Data;

namespace ForgeFlow.Sim;

public sealed partial class FactorySim : ISimSource
{
    // ══════════════════════════════════════════════════════════════════════════
    // 图集映射 —— sierrassets「Pixel Art Automation Tileset」
    //   图集：automation spritesheet.png，320×3776 = 10 列 × 118 行，**32×32 瓦片**
    //   以下行/列号全部**逐格目视 + 像素哈希 + 内容包围盒实测**核对过。
    //
    //   ⚠ 这套美术里**机器与物品的内容都不在单元格中心**：
    //     机器内容只在行 15 上（实测 x 28..66, y 2..31），物品图标画在各自单元格的左上角。
    //     所以「一格对一格」直接画必然错位 —— 必须按实测的内容子矩形取样。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>图集列数（320/32）。</summary>
    public const int AtlasCols = 10;

    /// <summary>图集行数（3776/32）。</summary>
    public const int AtlasRows = 118;

    public const int AtlasCells = AtlasCols * AtlasRows;

    /// <summary>
    /// 直线水平带面。**这套美术的带子是静态的**（行 5 的 10 张瓦片像素完全相同，
    /// 已用哈希确认），整条带子靠平铺同一张瓦片构成。
    /// ⇒ **动画必须靠滚动 UV 做**，不是靠换帧。这正是「三层分离」里缺的中间层。
    /// </summary>
    private const int BeltTileRow = 5;
    private const int BeltTileCol = 0;

    // ── 自动铺瓦：连通掩码 → 图集格 ─────────────────────────────────────────
    //
    // 掩码位：N=1 E=2 S=4 W=8。
    //
    // ⚠ 这张表是**算出来又核对过**的，不是猜的：
    //   1. 用「先看这条边有没有绿色路轨（有 ⇒ 带面不从这里穿过），再看向内 3~4px 是否连续带面」
    //      判定每条边的连通性；
    //   2. **自检**：(5,0) 必须得 0A（水平直线）、(6,0) 必须得 05（垂直直线）——
    //      这两个是先高倍渲染确认过形状的，分类器必须能还原它们，否则表就是错的；
    //   3. 掩码表本身与渲染图逐格目视核对。
    //
    // 这套美术的带子区（行 5..13）只提供 **2 种直线 + 4 种拐角 + 1 种十字**，
    // 没有 T 型与端头 —— 对我们的一进一出单车道带来说刚好够用。
    private static readonly int[] BeltTileRowByMask = new int[16];
    private static readonly int[] BeltTileColByMask = new int[16];

    static FactorySim()
    {
        // 默认全部退回水平直线（端头等用不到的形状就画成直线，总比空着好）
        for (int m = 0; m < 16; m++)
        {
            BeltTileRowByMask[m] = BeltTileRow;
            BeltTileColByMask[m] = BeltTileCol;
        }

        void Set(int mask, int row, int col)
        {
            BeltTileRowByMask[mask] = row;
            BeltTileColByMask[mask] = col;
        }

        Set(0x0A, 5, 0);    // E+W  水平直线
        Set(0x05, 6, 0);    // N+S  垂直直线
        Set(0x03, 9, 8);    // N+E  拐角
        Set(0x09, 10, 6);   // N+W  拐角
        Set(0x06, 11, 4);   // E+S  拐角
        Set(0x0C, 12, 2);   // S+W  拐角
        Set(0x0F, 13, 0);   // 十字（单车道用不到，留着备用）

        // 端头（只有一个邻居）：按邻居所在轴选直线
        Set(0x01, 6, 0);    // N
        Set(0x04, 6, 0);    // S
        Set(0x02, 5, 0);    // E
        Set(0x08, 5, 0);    // W
    }

    /// <summary>把连通掩码换成图集精灵 id。</summary>
    private static ushort BeltSpriteForMask(int mask)
        => (ushort)(BeltTileRowByMask[mask] * AtlasCols + BeltTileColByMask[mask]);

    /// <summary>
    /// 机器**兜底**的图集取样框：实测内容包围盒 = x 28..66, y 2..31（相对行 15 的起点）——
    /// 即 **39×30 的一块，且只落在行 15 上**（**不是** 2 行高）。
    /// 取一个包住它的 40×40 方块（圆心对准内容中心）⇒ 一次实例就画完，
    /// 不必切子瓦片，而且内容与实例中心天然对齐。
    ///
    /// 具体每台机器用哪个框由 <see cref="MachineArt"/> 带（各类机器大小不一），
    /// 这两个常量只用于**幽灵预览的兜底**。
    /// </summary>
    private const int MachineRow = 15;
    private const float MachineBoxX = 27f;
    private const float MachineBoxTopY = MachineRow * 32f - 3.5f;
    private const float MachineBoxSize = 40f;

    /// <summary>带面实例的世界边长 = 一格。</summary>
    public const float BeltWorldSize = 32f;

    /// <summary>
    /// 机器美术相对**原始像素**的缩放系数。**1.0 = 1 美术像素画 1 世界像素**（不改长宽比）。
    ///
    /// ⚠ **不要**按「把内容的最长边缩到正好一格」算：那会**破坏长宽比** ——
    ///   47×31 的熔炉被横向压掉 47%、18×51 的洗矿机被压成 11px 宽的细棍，
    ///   画面上就是「机器的纹理不对」。1.0 = 原始比例，形状正确，
    ///   代价是机器会略微越出自己那一格（这正是其他工厂游戏的样子）。
    ///   想缩回格内就调小（约 0.8 时 39px 的美术基本贴格）。
    /// </summary>
    public const float MachineFillRatio = 1.0f;

    // 下面几个是给「幽灵预览」用的图集坐标（与 BuildSpriteLut 里的机器块是同一块）。
    // Sim 项目不依赖 Godot，所以这里只暴露裸数值，由脚本侧拼成 Rect2。
    public const float MachineAtlasX = MachineBoxX;
    public const float MachineAtlasY = MachineBoxTopY;
    public const float MachineAtlasSize = MachineBoxSize;
    public const int BeltAtlasX = BeltTileCol * 32;
    public const int BeltAtlasY = BeltTileRow * 32;

    /// <summary>
    /// 物品图标的**逐格实测内容框**（**绝对图集坐标**，左上角锚点）。
    ///
    /// ⚠ 这套美术把每个图标画在各自 32×32 单元格的**左上角**，而且内容大小差不少
    ///   （7×7 的圆点 .. 14×14 的方块）。**不能**对全部物品用同一个「左上角 14×14」取样框，
    ///   那样：① 12×11 的图标在 14×14 的框里偏向左上 ⇒ 画出来偏左上；
    ///        ② 7×7 的圆点只占取样框的四分之一 ⇒ **只有别的物品一半大**。
    ///   每个图标必须按自己的内容框取样：取样框 == 内容框，图标天然居中、不被裁、不串邻格。
    ///
    /// ⚠ **数字必须由 `python tools/measure_art.py` 量出来，而且必须是绝对图集坐标。**
    ///   把「格内相对坐标」直接当成绝对坐标贴，第 0 行的四个图标 x 就全成了 0
    ///   ⇒ 四个物品**全都去采样第 0 列的内容**（铁矿画成了青色方块）。
    ///   自洽的探针抓不到这种错（探针只比「LUT 与这张表一致」，表本身错了它看不出来），
    ///   所以量具（measure_art.py 会直接把 C# 行打出来）与截图判据（verify_render_orientation.py ④）
    ///   都必须覆盖它。
    ///
    /// ⚠ **第二层坑**：这张表**只对 <see cref="ItemIconCells"/> 里的格子生效**
    ///   （见 <see cref="FillSpriteTables"/>）—— 不在表里的格子连 LUT 都不会填，
    ///   会退回「整格取样 + 整格尺寸」：画出来是 32×32 的透明方块，
    ///   图标缩在左上角一小块、下半截透出带面。
    ///   **「主色对不对」不足以说明能用**：格的「可采样性」是另一回事。
    ///   所以 <see cref="ContentDb.DefaultItems"/> 的 <c>IconCell</c>
    ///   **只能从 <see cref="ItemIconCells"/> 里挑**。
    /// </summary>
    private static readonly (ushort Cell, float X, float Y, float W, float H)[] ItemIconBoxes =
    {
        (0 * AtlasCols + 1,  32f,   0f, 12f, 11f),   // 蓝色方块
        (0 * AtlasCols + 3,  96f,   0f,  7f,  7f),   // 紫色圆点
        (0 * AtlasCols + 5, 160f,   0f,  7f,  7f),   // 浅蓝圆点
        (0 * AtlasCols + 7, 224f,   0f, 14f, 14f),   // 灰色六边形
        (1 * AtlasCols + 7, 224f,  32f, 12f, 11f),   // 锈铜方块
        (1 * AtlasCols + 9, 288f,  32f, 12f, 11f),   // 黄色板
        (2 * AtlasCols + 0,   0f,  64f, 12f, 11f),   // 绿色电路板（格 0 的 x 是 0，不是「格内相对坐标」）
        (2 * AtlasCols + 1,  32f,  64f, 12f, 11f),   // 青色方块
        (2 * AtlasCols + 2,  64f,  64f, 12f, 11f),   // 锈铜斜纹板
        (2 * AtlasCols + 3,  96f,  64f, 12f, 11f),   // 钢蓝斜纹板（格 3 的 x 是 96 = 3×32）
        (2 * AtlasCols + 7, 224f,  64f, 12f, 11f),   // 黄色箱
        (3 * AtlasCols + 1,  32f,  96f, 12f, 11f),   // 灰色齿轮
    };

    /// <summary>
    /// 物品图标的统一放大倍数。**全部物品用同一个倍数** —— 这就是「统一比例」：
    /// 画面上大小的差别只来自美术本身（7×7 的圆点就是比 14×14 的方块小一半），
    /// **不要**单独放大某几种物品：那会让它们被自己的取样框切掉一半。
    /// 2× 是折中：最大的 14×14 画成 28px（仍在 32px 的一格内），12×11 画成 24×22。
    /// </summary>
    public const float ItemIconScale = 2f;

    /// <summary>某个物品图标格子的实测内容框（左上角 + 宽高，绝对图集坐标）。</summary>
    public static bool TryItemIconBox(ushort cell, out float x, out float y, out float w, out float h)
    {
        foreach ((ushort Cell, float X, float Y, float W, float H) box in ItemIconBoxes)
        {
            if (box.Cell != cell) continue;
            (x, y, w, h) = (box.X, box.Y, box.W, box.H);
            return true;
        }
        (x, y, w, h) = (0f, 0f, ItemBoxFallback, ItemBoxFallback);
        return false;
    }

    /// <summary>
    /// 没有登记实测内容框时的兜底取样边长（= 「所有图标共用左上角 14×14」的那个框）。
    /// 留着是为了「未知物品类型」不至于画不出来，但**它是错的**（小图标只有一半大、内容偏左上），
    /// 所以探针 12 会盯着「ItemIconCells 里的每一项都必须登记」。
    /// </summary>
    private const float ItemBoxFallback = 14f;

    /// <summary>
    /// 物品图标候选（行 0..3，10 列/行）—— 这套美术把图标画在单元格左上角，
    /// 所以**只有这些格子**适合当物品图标；<see cref="ContentDb"/> 的
    /// <see cref="ItemDef.IconCell"/> 必须从这里挑。
    ///
    /// ⚠ 它同时是「未知物品类型」的兜底轮换表（探针与演示布局会造出内容表里没有的 id），
    ///   所以每一项都必须在 <see cref="ItemIconBoxes"/> 里有实测内容框 —— 不是的话物品会
    ///   退回整格取样、又变小又偏左上（探针 12 会盯着这条）。
    ///
    /// ⚠ 另一个隐式前提：<see cref="FillSpriteTables"/> 是**先填机器美术、后用这张表覆盖**的，
    ///   所以**物品格绝不能与任何机器美术格相同** —— 不然那台机器会被按「图标内容框」取样，
    ///   画面上是个小图标。这套包的机器都在第 15 行往后（格号 ≥ 150），物品都在第 0..3 行，
    ///   天然不重叠；加新机器美术时别往 0..3 行放。
    /// </summary>
    public static readonly ushort[] ItemIconCells =
    {
        0 * AtlasCols + 1,   // 蓝色方块
        0 * AtlasCols + 3,   // 紫色圆点
        0 * AtlasCols + 5,   // 浅蓝圆点
        0 * AtlasCols + 7,   // 灰色六边形
        1 * AtlasCols + 7,   // 锈铜方块
        1 * AtlasCols + 9,   // 黄色板
        2 * AtlasCols + 0,   // 绿色电路板
        2 * AtlasCols + 1,   // 青色方块
        2 * AtlasCols + 2,   // 锈铜斜纹板
        2 * AtlasCols + 3,   // 钢蓝斜纹板
        2 * AtlasCols + 7,   // 黄色箱
        3 * AtlasCols + 1,   // 灰色齿轮
    };

    private static ushort SpriteBelt => (ushort)(BeltTileRow * AtlasCols + BeltTileCol);

    /// <summary>
    /// 管道瓦片 —— **占位，不是真管道美术**。
    ///
    /// 这套包**没有专用管道素材**（我逐格渲染核对过：窄条候选其实是机器附件的瓦片）。
    /// 这里用 (15,0) 的细灰竖条当**可见标记**，好让管网的走向在画面上看得出来。
    /// 横向管道会显得不对（没有旋转），正式做之前需要补管道素材或改用程序化管道。
    /// </summary>
    private const int PipeTileRow = 15;
    private const int PipeTileCol = 0;
    private static ushort SpritePipe => (ushort)(PipeTileRow * AtlasCols + PipeTileCol);

    /// <summary>管道占位瓦片的图集格号（预览层重画管道时用同一张）。</summary>
    public const int PipeAtlasCell = PipeTileRow * AtlasCols + PipeTileCol;

    /// <summary>机器朝向的最大帧数（分流器那台有 4 帧，见 <see cref="MachineArt.FrameStride"/>）。</summary>
    public const int MaxMachineFrames = 4;

    /// <summary>
    /// **一段带里的某一格：该用哪张瓦片 + 朝向转多少**（纯函数：只看路径，不看任何状态）。
    ///
    /// 玩家要的是「这一段是上下还是左右」由**起点 → 终点（含中间点）**决定，所以：
    ///   · 瓦片形状（直线 / 拐角 / 端头）由**与相邻两格的连线**算出的连通掩码选（自动铺瓦）；
    ///   · 直线的**箭头朝向**再由**流向**决定：往东/往南 = 0，往西/往北 = 180°。
    ///     这套美术只有「向右」「向下」两张直线瓦片，反向的段只能靠旋转 180° 才让箭头对得上流向
    ///     （**不要**固定 rot=0：往西的带箭头指着东，物品却往西走 —— 动起来就是「纹路和货打架」）。
    /// 拐角**不旋转**：拐角瓦片的形状本身已经编码了转向（4 个掩码对应 4 种拐法）。
    ///
    /// ⚠ 这个函数是**铺瓦的唯一真相**：实际渲染（<see cref="RebuildBeltTiles"/>）、蓝图粘贴预览
    ///   与拖动铺带的预览全都调它 —— 各写一份的话，预览里的拐角迟早和铺出来的不一样。
    /// </summary>
    public static (ushort Sprite, float Rot) BeltTileAt(GridPos[] cells, int k)
    {
        int mask = 0;
        if (k > 0) mask |= DirBit(cells[k - 1], cells[k]);              // 上一格在本格的哪一侧
        if (k + 1 < cells.Length) mask |= DirBit(cells[k + 1], cells[k]);

        ushort sprite = BeltSpriteForMask(mask);

        bool straight = IsStraightMask(mask);
        if (!straight || cells.Length < 2) return (sprite, 0f);

        // 流向：中段看「本格 → 下一格」，末格没有下一格，就看「上一格 → 本格」。
        GridPos from = k + 1 < cells.Length ? cells[k] : cells[k - 1];
        GridPos to = k + 1 < cells.Length ? cells[k + 1] : cells[k];
        bool reverse = to.X < from.X || to.Y < from.Y;                 // 往西 / 往北
        return (sprite, reverse ? MathF.PI : 0f);
    }

    /// <summary>邻居在本格的哪个方位（N=0x1 E=0x2 S=0x4 W=0x8）—— 与连通掩码同一套位。</summary>
    private static int DirBit(GridPos neighbour, GridPos self)
    {
        if (neighbour.Y < self.Y) return 0x1;   // N
        if (neighbour.Y > self.Y) return 0x4;   // S
        if (neighbour.X > self.X) return 0x2;   // E
        return 0x8;                             // W
    }

    /// <summary>
    /// 这个连通掩码是不是**直线**（没有转弯）。
    ///
    /// ⚠ **只此一份**：<see cref="BeltTileAt"/>（决定箭头朝向）与 <c>BeltCellIsCorner</c>
    ///   （决定拐角格要不要滚动纹路）都要判它。两处各写一遍的话，改一处漏一处 ——
    ///   而它们的后果完全不同：一个让箭头指着反向，一个让拐角纹路与两侧直线段撕裂。
    /// </summary>
    private static bool IsStraightMask(int mask)
        => mask is 0x00 or 0x0A or 0x05 or 0x01 or 0x04 or 0x02 or 0x08;

    /// <summary>
    /// 逐格算出带面该用哪张瓦片与朝向（自动铺瓦）。**只在结构变更时算一次，不是每 tick。**
    ///
    /// 连通性只看同一段带内相邻的格子 —— 因为我们的带是一进一出的单车道路径，
    /// 玩家画出来的形状天然就是「直线 / 拐角 / 端头」。算法本体在
    /// <see cref="BeltTileAt"/>（预览与粘贴也复用同一份）。
    /// </summary>
    private void RebuildBeltTiles()
    {
        for (int b = 0; b < _beltSlots; b++)
        {
            if (!_beltAlive[b]) { _beltCellSprites[b] = null; _beltCellRot[b] = null; continue; }
            GridPos[] cells = _layout.BeltAt(b)!.Cells;
            var sprites = new ushort[cells.Length];
            var rots = new float[cells.Length];

            for (int k = 0; k < cells.Length; k++)
            {
                (ushort sprite, float rot) = BeltTileAt(cells, k);
                sprites[k] = sprite;
                rots[k] = rot;
            }

            _beltCellSprites[b] = sprites;
            _beltCellRot[b] = rots;

            // 剔除用包围盒：**在这里算**，因为这里正是「结构变了」的唯一入口
            //（RebuildBeltTiles 跟随 SyncTopology 走，见那里的调用点）。
            // 放到快照热路径里算就等于每 tick 重新遍历一遍全部格子 —— 那正是要省掉的钱。
            // 一格 32px，取格心 ±半格；再留一格余量，避免边缘上的实例因为
            // 「中心刚好在视口外、但身子还压着视口」而被剔除掉（症状是边缘闪空）。
            RecomputeBeltBounds(b, cells);
        }
    }

    /// <summary>算一段带的包围盒（世界像素）。见 <see cref="RebuildBeltTiles"/> 的说明。</summary>
    private void RecomputeBeltBounds(int b, GridPos[] cells)
    {
        if (cells.Length == 0)
        {
            _beltBounds[b] = default;
            return;
        }

        const float half = FactoryLayout.CellPx * 0.5f;
        float loX = float.MaxValue, loY = float.MaxValue;
        float hiX = float.MinValue, hiY = float.MinValue;
        foreach (GridPos c in cells)
        {
            (float wx, float wy) = FactoryLayout.GridToWorld(c);
            if (wx < loX) loX = wx;
            if (wx > hiX) hiX = wx;
            if (wy < loY) loY = wy;
            if (wy > hiY) hiY = wy;
        }
        _beltBounds[b] = new WorldRect(loX - half, loY - half, hiX + half, hiY + half);
    }

    /// <summary>把「带→带」连接缓存成一张下标表（只随结构变更重建，不在热路径上查字典）。</summary>
    private void RebuildBeltLinks()
    {
        for (int b = 0; b < _beltSlots; b++)
            _beltDownstream[b] = _beltAlive[b] ? (_layout.BeltAt(b)?.ToBelt ?? -1) : -1;
    }

    /// <summary>
    /// 机器第 f 帧的精灵 id。帧之间在图上相隔 <paramref name="frameStride"/> 行。
    /// <paramref name="frameStride"/> = 0 ⇒ 静态贴图，f 被忽略。
    /// </summary>
    public static ushort MachineFrameCell(int spriteCell, int frameStride, int frame)
        => (ushort)(spriteCell + frame * frameStride * AtlasCols);

    /// <summary>一行图集里某个 32px 单元格的 UV 原点。</summary>
    private static (float U, float V) CellOrigin(int row, int col)
        => ((float)(col * 32) / (AtlasCols * 32), (float)(row * 32) / (AtlasRows * 32));

    /// <summary>
    /// 把 <see cref="MachineArt"/> 写进 UV 表与尺寸表（含动画帧）。
    ///
    /// UV 表每格 4 个 float：**(U 原点, V 原点, X 向取样缩放, Y 向取样缩放)** ——
    /// 两个方向的缩放必须分开给，否则**非方形**的内容框没法精确取样
    /// （机械臂 27×41、水塔 39×40、洗矿机 18×51）：**不能**只给一个 uniform 缩放 ——
    /// 那会退化成「边长 = max(w,h) 的正方形框且锚在左上角」⇒ 内容偏出中心、还串进右边邻格。
    ///
    /// 尺寸表每格 2 个 float：世界宽高，**单位是「格」**（1.0 = 一格 = 32px）。
    /// 取法是「把内容的最大边缩到一格、另一边按比例」，于是**每个物块都正好落在一格内** ——
    /// 这正是「保持每个物块和网格对齐」的实现处（<see cref="MachineFillRatio"/> 可微调）。
    /// </summary>
    private static void FillMachineArt(float[] uvLut, float[] sizeLut, MachineArt art)
    {
        int frames = art.FrameCount;
        for (int f = 0; f < frames; f++)
        {
            int cell = art.Cell + f * art.FrameStride * AtlasCols;
            if ((uint)cell >= (uint)AtlasCells) break;

            (float x, float y, float w, float h) = art.FrameRect(f);

            uvLut[cell * 4] = x / (AtlasCols * 32);
            uvLut[cell * 4 + 1] = y / (AtlasRows * 32);
            uvLut[cell * 4 + 2] = w / 32f;
            uvLut[cell * 4 + 3] = h / 32f;

            // ⚠⚠ **不要**按「最长边缩到一格」算（`MachineFillRatio * w / longest`）：
            //   那会**破坏长宽比** —— 宽机器被横向压扁（47×31 → 1.00×0.66，横向压 47%），
            //   高机器被压成细条（洗矿机 18×51 → 0.35×1.00，画出来只有 11px 宽）。
            //
            //   这里用**一格 = 32 世界像素的原始比例**：`MachineFillRatio = 1.0` 就是 1:1。
            //   代价是机器会**略微越出自己那一格**（39px 的美术画在 32px 的格上 = 1.22 格），
            //   相邻机器/带面会有一点重叠 —— 但这正是其他工厂游戏的做法（机器视觉上比占位大）。
            //   想让它缩回格内就把 MachineFillRatio 调小（0.8 左右基本就贴格了）。
            sizeLut[cell * 2] = MachineFillRatio * w / CellSizePx;
            sizeLut[cell * 2 + 1] = MachineFillRatio * h / CellSizePx;
        }
    }

    /// <summary>
    /// 逐精灵的图集映射：**(U 原点, V 原点, X 取样缩放, Y 取样缩放)**，索引 = 精灵 id，
    /// 共 <see cref="AtlasCells"/> × 4 项。
    ///
    /// 默认 = 整格取样。带面 / 机器 / 物品各自覆盖成**实测的内容矩形** ——
    /// 这套美术里机器和物品的内容都不在单元格中心，所以「一格对一格」直接画必然错位。
    /// 桥接层用这张表把内容**居中且不跨格**地呈现出来。
    /// </summary>
    /// <param name="content">内容表（机器美术在这）。null = 用内置表。</param>
    public static float[] BuildSpriteLut(ContentDb? content = null)
    {
        var lut = new float[AtlasCells * 4];
        FillSpriteTables(lut, new float[AtlasCells * 2], content ?? ContentDb.Default);
        return lut;
    }

    /// <summary>
    /// 逐精灵的**世界尺寸**（单位 = 格；1.0 就是一格）。渲染时乘上基础边长得到实例大小。
    ///
    /// 为什么必须逐精灵给：各类机器在图上大小差很多（18×51 .. 47×31）。
    /// 这里按**实测内容框 ÷ 一格像素**登记（即 1:1，见 <see cref="FillSpriteTables"/>），
    /// 所以机器的视觉尺寸会**略微越出自己那一格**（39px 的美术画在 32px 的格上）。
    /// 想缩回格内就调小 <c>MachineFillRatio</c>（0.8 左右基本就贴格了）。
    /// 默认 (1,1) ⇒ 带面与管道不用登记。
    /// </summary>
    public static float[] BuildSpriteSizeLut(ContentDb? content = null)
    {
        var sizes = new float[AtlasCells * 2];
        FillSpriteTables(new float[AtlasCells * 4], sizes, content ?? ContentDb.Default);
        return sizes;
    }

    /// <summary>
    /// **两张表的唯一填充处。**
    ///
    /// ⚠ 为什么必须合成一处：渲染侧是**分别**调 <see cref="BuildSpriteLut"/>（取样框）与
    ///   <see cref="BuildSpriteSizeLut"/>（世界尺寸）的。物品图标的尺寸只要写在其中一张的
    ///   内部临时表里（转身就丢），渲染拿到的那张就还是默认 1.0 格 ⇒
    ///   「物品图标 2× 统一比例」不会生效，物品被拉满整格（12×11 的内容被拉到 32×29，且**变形**）。
    ///   探针 12 就是靠「两张表分别取出来对不上」把这条抓出来的。
    /// </summary>
    private static void FillSpriteTables(float[] uvLut, float[] sizeLut, ContentDb content)
    {
        for (int i = 0; i < AtlasCells; i++)
        {
            (float u, float v) = CellOrigin(i / AtlasCols, i % AtlasCols);
            uvLut[i * 4] = u;
            uvLut[i * 4 + 1] = v;
            uvLut[i * 4 + 2] = 1f;      // 整格
            uvLut[i * 4 + 3] = 1f;
            sizeLut[i * 2] = 1f;
            sizeLut[i * 2 + 1] = 1f;
        }

        foreach (BuildableDef b in content.Buildables)
            FillMachineArt(uvLut, sizeLut, b.Art);

        foreach (ushort cell in ItemIconCells)
        {
            // 逐格实测内容框；没登记的格子退回兜底框（探针 12 盯着这条）
            TryItemIconBox(cell, out float x, out float y, out float w, out float h);
            // 取样框 = 内容框本身（逐轴），所以图标在实例里天然居中；尺寸 = 内容 × 统一倍数。
            uvLut[cell * 4] = x / (AtlasCols * 32);
            uvLut[cell * 4 + 1] = y / (AtlasRows * 32);
            uvLut[cell * 4 + 2] = w / 32f;
            uvLut[cell * 4 + 3] = h / 32f;
            sizeLut[cell * 2] = ItemIconScale * w / CellSizePx;
            sizeLut[cell * 2 + 1] = ItemIconScale * h / CellSizePx;
        }
    }

}
