using Godot;
using ForgeFlow.Sim;
using ForgeFlow.Sim.Data;

/// <summary>
/// **机器名 label 的通用绘制器** —— 主场景与压力场景共用同一份实现。
///
/// 用法（两个场景完全一样）：
/// <code>
///   _labels = new MachineLabelDrawer(sim.MachineLabels);   // 数据来自模拟侧
///   ...
///   public override void _Draw() =&gt; _labels.Draw(this, _worldRect, GetViewportTransform());
///   // 相机一动就 QueueRedraw（见 RequestRedraw）
/// </code>
///
/// 设计要点（每条都对应一次踩过的坑）：
///   · **数据从模拟侧来**（<see cref="MachineLabelBoard"/>）：渲染线程不去读 <c>_layout</c>，
///     名字由内容表按图集格反查 ⇒ 「机器自己的信息」，任何场景通用，不用各写一份表。
///   · **不建任何节点**：走 <c>CanvasItem._Draw</c> ── 符合项目「节点一律在场景里预建」的硬要求。
///   · **只在正常/放大时画**（<see cref="MinZoom"/>）：拉远时几千个字叠在一起，比不画更糟。
///   · **单帧上限**（<see cref="Cap"/>）：极端视野下别把帧时间吃光。
///   · 画在机器**上方且水平居中** —— 左对齐会让文字从机器中心往右跑，像在标注右边那台。
///   · 字号是**屏幕像素**，靠 <c>DrawSetTransform(scale: 1/画布缩放)</c> 抵消画布缩放来保证。
///     不这么做的话「按 15/zoom 栅格化、再被画布放大 zoom 倍」在放大时就是一团灰糊
///     （zoom=4 时栅格只有 3.75px，还被下限卡到 6px）。细节见 <see cref="Draw"/> 里的注释。
///
/// ⚠ z 序：调用方必须保证 `_Draw` 所在节点的 z **高于实例层**，否则 label 会被带面/机器盖住。
///   两个场景都已显式设好（根节点抬高 + 子节点 `z_as_relative = false`）。
/// </summary>
public sealed class MachineLabelDrawer
{
    private readonly MachineLabelBoard _board;

    /// <summary>低于这个缩放就不画（字会挤成一片）。</summary>
    public float MinZoom { get; set; } = 0.35f;

    /// <summary>一帧最多画多少个。</summary>
    public int Cap { get; set; } = 220;

    /// <summary>
    /// **场景自己加的静态标签**（例如主场景的「物品陈列区」）。
    /// 这些是场景在建图阶段就知道的常量，不来自模拟侧，所以在构建期一次性塞进来即可 ——
    /// 构建发生在模拟线程启动之前，之后这张表只归渲染线程，没有竞争。
    /// </summary>
    private readonly System.Collections.Generic.List<MachineLabelBoard.Entry> _extra = new();

    /// <summary>加一条静态标签（世界坐标 + 文字）。</summary>
    public void AddStatic(float x, float y, string name)
        => _extra.Add(new MachineLabelBoard.Entry(x, y, name));

    /// <summary>
    /// 屏幕上大约多少像素高（会按缩放折算回画布局部单位）。
    /// ⚠ 中文小字在 13px 已经发虚，15px 才读得舒服。
    /// </summary>
    public float ScreenFontPx { get; set; } = 15f;

    public Color Color { get; set; } = new(1f, 1f, 1f, 0.85f);

    /// <summary>总开关（环境变量可关，方便出「无 label」对照截图）。</summary>
    public bool Enabled { get; set; } =
        System.Environment.GetEnvironmentVariable("FORGEFLOW_NO_LABELS") != "1";

    /// <summary>上一次画了几个（诊断/验证用：为 0 说明这台机器一台都没在视野里）。</summary>
    public int LastDrawn { get; private set; }

    public MachineLabelDrawer(MachineLabelBoard board) => _board = board;

    /// <summary>
    /// **要不要排队重画？**
    ///
    /// ⚠ 这里有个容易写错的地方：不能简单地写成 `zoom >= MinZoom`。
    ///   拉远到阈值以下时如果**不再重画**，画布上**最后一次画的 label 会留在屏幕上**
    ///   （`_Draw` 不再被调用，旧内容就没人清）—— 症状正是「缩小后 label 还在」。
    ///   ⇒ 判据是「还会画」**或者**「上一次画过东西（需要一次重画来清掉）」。
    /// </summary>
    public bool ShouldRedraw(float zoom) => Enabled && (zoom >= MinZoom || LastDrawn > 0);

    public void Draw(CanvasItem target, Rect2 view, Transform2D worldToScreen)
    {
        LastDrawn = 0;                                  // 先清零：下面任何一条早退都等于「这帧没画」
        if (!Enabled) return;
        float scale = worldToScreen.Scale.X;             // 画布变换的缩放 = 相机 zoom × 窗口拉伸
        if (scale <= 0.0001f) return;
        if (scale < MinZoom) return;                    // 拉远：不画（并靠 ShouldRedraw 那一次重画清掉旧的）
        if (view.Size.X <= 0f) return;

        MachineLabelBoard.Entry[] items = _board.Snapshot();

        Font font = ThemeDB.FallbackFont;
        // 字号是**屏幕像素**，恒定，与相机缩放无关（见下面 DrawSetTransform 那段注释）。
        int size = Math.Clamp((int)MathF.Round(ScreenFontPx), 8, 64);
        float inv = 1f / scale;

        int drawn = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            // pass 0 = 场景自己加的静态标签；pass 1 = 模拟侧发布的机器名
            int count = pass == 0 ? _extra.Count : items.Length;
            if (count == 0) continue;

            for (int i = 0; i < count; i++)
            {
                // ⚠ 上限判据要 `>=` 且在**画之前**。原来写的是 `if (++drawn > Cap) break;`，两个毛病：
                //   ① 实际画出 Cap 个，却把 drawn 记成 Cap+1（LastDrawn 是诊断量，报的是错的）；
                //   ② `break` 只跳出**内层**循环，外层 pass 1 照跑，而此时 drawn 已经 > Cap
                //      ⇒ **机器名一个都轮不上**（pass 0 的静态标签把配额吃光了）。
                if (drawn >= Cap) break;
                MachineLabelBoard.Entry e = pass == 0 ? _extra[i] : items[i];
                var world = new Vector2(e.X, e.Y);
                if (!view.HasPoint(world)) continue;

                // ⚠ **必须把画布变换里的缩放抵消掉。**
                //   **不要**写成「字号 = 15 / zoom，交给画布再乘 zoom 回去」—— 那样**净尺寸**是对的，
                //   但**字模是按 15/zoom 像素栅格化的**：zoom=4 时只有 3.75px，被 6px 下限卡住，
                //   于是 6px 的字模被放大 4 倍 ⇒ 放大后是一片灰糊，不是笔画。
                //   矢量的 CJK 字**没法靠放大变清楚** —— 唯一的办法是让它按最终屏幕像素栅格化：
                //   自定义变换 scale = 1/scale，画布再乘 scale，净缩放恰好 1。
                //
                //   ⚠ 原点给的是**世界坐标**：`DrawSetTransform` 换掉的是**本节点自己的变换**，
                //     外面的画布变换（相机 + 拉伸）照旧再乘一次 ⇒ 原点必须留在世界空间，
                //     给屏幕坐标会被画布变换**再乘一遍**。前提是本节点自己的变换是单位阵
                //     （两个场景的绘制节点都是场景根节点）—— 若哪天把它挂到别的节点下面，
                //     label 会整体偏掉，这是第一个要查的地方。
                target.DrawSetTransform(world, 0f, new Vector2(inv, inv));

                Vector2 sz = font.GetStringSize(e.Name, HorizontalAlignment.Left, -1f, size);
                // 局部坐标此时就是屏幕像素（净缩放 = 1）⇒ 取整即落在整像素上，字模不再被重采样。
                // 「上移 0.85 格」是世界量，要乘 scale 才换算成屏幕像素。
                var at = new Vector2(MathF.Round(sz.X * -0.5f),
                                     MathF.Round(FactorySim.CellSizePx * -0.85f * scale));
                target.DrawString(font, at, e.Name, HorizontalAlignment.Left, -1f, size, Color);
                drawn++;                       // 只在**真的画了**之后计数（见上面的上限判据）
            }
        }
        LastDrawn = drawn;
    }
}
