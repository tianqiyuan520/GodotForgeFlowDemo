using System;
using Godot;

using Environment = System.Environment;

// =============================================================================
// CameraController —— 工厂相机（平移 / 缩放 / 拖拽）。
//
// ⚠ 工程约定：本脚本挂在**场景里预先建好的 Camera2D** 上（Scenes/Main.tscn），
//   不 new 节点、不 AddChild。
//
// 为什么需要它（而不是把相机写死在场景里）：
//   世界单位 = 图集原生瓦片 = 32px，一格就是一瓦片。**一屏 1920×1080 只能看见 60×34 格**，
//   随便搭一条像样的产线就出屏了。在造出相机控制之前，玩家只能通过改场景文件
//   换 position 来看别处 —— 那显然不是「能玩」。
//
// 三层操作，从粗到细：
//   ① 键盘 WASD / 方向键 —— 匀速平移，速度**按当前缩放折算**，
//      所以放大之后微调也依然是「屏幕上看起来一样快」；
//   ② 鼠标滚轮 —— 以**光标位置为锚点**缩放（放大时光标下的那一格不动），
//      这是地图类工具的正确手感；「以屏幕中心缩放」会让玩家每次都要重新找回目标；
//   ③ 中键拖拽 —— 抓取式平移（抓住地图往哪拖，地图就往哪走）。
//
// 缩放走整数倍优先（1/2/3/4）而不是连续值：
//   像素画在非整数缩放下会出现宽窄不一的像素（nearest 采样下的经典瑕疵），
//   所以滚轮在若干「档位」之间跳。档位里也留了 0.5 与 1.5 供远看全景。
// =============================================================================

public partial class CameraController : Camera2D
{
    /// <summary>
    /// 可选的缩放档位（越大越近）。
    ///
    /// ⚠ **向下一直留到 0.01**：地图类场景的世界尺寸取决于工厂规模，而「看得见多少」= 视口 ÷ 缩放。
    ///   演示场景只有 30×20 格，百万场景的世界却有 2400×1380 格 —— 档位不够小就**拉不出全景**
    ///   （相机取景的 minZoom 就是靠它拿到的：见 `StressRunner` 的 FORGEFLOW_STRESS_FIT）。
    ///
    /// ⚠ **但 0.05 以下就是"亚像素"**：一格 = 32px × zoom，zoom < 1/32（≈0.031）时
    ///   一张瓦片在屏幕上**不足一个像素**，引擎栅格化不出来，而 LOD 又会把它抽样成 `1/s²`
    ///   ⇒ 画面只剩网格、**轨道和机器看起来全都没了**。
    ///   这不是渲染坏了，是物理限制，所以：
    ///     ① 档位保留（用户要求"还能再缩小" —— 拉全景是真实需求）；
    ///     ② 一旦进入亚像素，<see cref="RefreshSubPixelFlag"/> 会**明确打印原因**，
    ///        HUD 的建造行也会写「亚像素取景 · 看不到轨道/机器是预期的 · 请放大」。
    ///   想看细节请放大到「一像素一格」以上（≥ 0.0312）；想看全貌就接受"只能看出厂区轮廓"。
    ///
    /// 各档大致能看多少（1920×1080 视口、32px 一格）：
    ///   1.0 ⇒ 60×34 格 ｜ 0.5 ⇒ 120×67 ｜ 0.05 ⇒ 1200×675 ｜ 0.02 ⇒ 3000×1688 ｜ 0.01 ⇒ 6000×3375
    /// </summary>
    private static readonly float[] ZoomSteps =
        { 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.35f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };

    /// <summary>一格在世界里最少要有几个屏幕像素才算「画得出来」（见 <see cref="ZoomSteps"/>）。</summary>
    public const float MinPixelsPerCell = 1f;

    /// <summary>当前是不是**亚像素**取景（一格不足 1 像素 ⇒ 轨道/机器基本看不见）。</summary>
    public bool SubPixel { get; private set; }

    /// <summary>可读的最低缩放（一格 = 1 像素）。</summary>
    public static float MinReadableZoom => MinPixelsPerCell / ForgeFlow.Sim.FactorySim.CellSizePx;

    /// <summary>键盘平移速度（屏幕像素/秒）—— 按缩放折算成世界单位。</summary>
    [Export] public float PanSpeedPx = 900f;

    /// <summary>中键拖拽的灵敏度（1 = 地图 1:1 跟手）。</summary>
    [Export] public float DragSensitivity = 1f;

    /// <summary>初始取景（用于 Home / F 复位）。**由 <see cref="FrameWorld"/> 按实际世界算出来**。</summary>
    [Export] public Vector2 HomePosition = new(900f, 430f);
    [Export] public float HomeZoom = 1f;

    private int _zoomIndex;              // _Ready 里按实际 Zoom 对齐到最近档位
    private bool _dragging;
    private bool _enabled = true;
    private readonly bool _camDiag =
        System.Environment.GetEnvironmentVariable("FORGEFLOW_CAMDIAG") == "1";

    /// <summary>
    /// **按实际世界包围盒取景**（居中 + 选一个合适的缩放），并把结果记为 Home。
    ///
    /// 为什么必须由调用方把世界尺寸传进来，而不是在这里写死：
    ///   世界有多大**取决于这一局建了多少东西**（演示场景 30×20 格、百万场景几百×几千格），
    ///   相机没有任何办法自己知道。⚠ **不要**把 Home 写死成某个坐标 + 缩放 1 ——
    ///   那等于假设「世界就是 1920×1080」，换一个规模就停在空地上或只见一角。
    ///
    /// <paramref name="viewportSize"/> 必须是**实际视口像素**（<c>GetViewportRect().Size</c>），
    /// 不能写死分辨率：全屏/不同显示器下它不一样，写死会让「看得见多少格」算错。
    ///
    /// 缩放取「刚好装得下整个世界」的档位，再夹在 [<paramref name="minZoom"/>,
    /// <paramref name="maxZoom"/>] 之间：
    ///   · 上限 = 1（原生像素，像素画最清楚）——世界很小时不放大到糊；
    ///   · 下限默认 0.5 —— 世界大到一定程度后**不追求一眼看完**（那样只会糊成一片），
    ///     而是给一个「能看清产线、又比原生看得多」的取景。想硬拉全景就把下限调小。
    /// </summary>
    public void FrameWorld(float loX, float loY, float hiX, float hiY, Vector2 viewportSize,
                           float minZoom = 0.5f, float maxZoom = 1f)
    {
        float w = MathF.Max(hiX - loX, 1f);
        float h = MathF.Max(hiY - loY, 1f);

        // 「装下整个世界」需要的缩放 = 两个轴里更紧的那个
        float fit = MathF.Min(viewportSize.X / w, viewportSize.Y / h);
        float z = Math.Clamp(fit, minZoom, maxZoom);

        Position = new Vector2((loX + hiX) * 0.5f, (loY + hiY) * 0.5f);
        Zoom = new Vector2(z, z);
        _zoomIndex = NearestZoomIndex(z);
        RefreshSubPixelFlag();

        // 记成 Home：复位（Home / F）回到这个贴合本局世界的取景，而不是场景文件里的死值
        HomePosition = Position;
        HomeZoom = z;
    }

    /// <summary>
    /// 缩放进/出「亚像素」时**把话说清楚**（控制台一行）。
    ///
    /// 为什么要专门做这件事：亚像素取景的症状是「画面上什么都没有」，而所有计数
    /// （写出实例 33,935 / interop 1/帧 / 无任何报错）看起来**一切正常**。
    /// 一行明确的日志能把这种人机误解直接掐掉，成本是零。
    /// </summary>
    private void RefreshSubPixelFlag()
    {
        float pxPerCell = Zoom.X * ForgeFlow.Sim.FactorySim.CellSizePx;
        bool sub = pxPerCell < MinPixelsPerCell;
        if (sub == SubPixel) return;
        SubPixel = sub;

        if (sub)
            GD.Print($"[camera] ⚠ 亚像素取景：缩放 {Zoom.X:F4} ⇒ 一格只有 {pxPerCell:F2} 屏幕像素（< 1）。" +
                     $"轨道/机器会按 LOD 抽样成点，屏幕上**基本看不到东西 —— 这是预期的，不是渲染坏了**" +
                     $"（一张 32px 瓦片不足一个像素，引擎栅格化不出来）。" +
                     $"要看清楚请放大到「一像素一格」以上（缩放 ≥ {MinReadableZoom:F4}）。");
        else
            GD.Print($"[camera] 缩放 {Zoom.X:F4}：一格 {pxPerCell:F2} 屏幕像素（可读）");
    }

    public override void _Ready()
    {
        HomePosition = Position;
        HomeZoom = Zoom.X;

        // 环境变量可以让自动化跑测固定住相机（截图对比需要可复现的画面）
        string? z = Environment.GetEnvironmentVariable("FORGEFLOW_ZOOM");
        if (!string.IsNullOrEmpty(z) && float.TryParse(z, out float zv) && zv > 0f)
            Zoom = new Vector2(zv, zv);

        if (Environment.GetEnvironmentVariable("FORGEFLOW_NO_CAMERA") == "1")
        {
            _enabled = false;
            GD.Print("[camera] 已按 FORGEFLOW_NO_CAMERA=1 停用相机控制");
        }

        // 初始对齐到最接近的档位，之后滚轮就在档位间走
        _zoomIndex = NearestZoomIndex(Zoom.X);
        RefreshSubPixelFlag();
        GD.Print($"[camera] 就绪：位置={Position} 缩放={Zoom.X}（档位 {string.Join(", ", ZoomSteps)}）");
        GD.Print($"[camera] WASD/方向键平移 · 滚轮缩放（以光标为锚点，档位 {ZoomSteps[0]}..{ZoomSteps[^1]}，" +
                 $"一格 {ZoomSteps[0] * ForgeFlow.Sim.FactorySim.CellSizePx:F2}.." +
                 $"{ZoomSteps[^1] * ForgeFlow.Sim.FactorySim.CellSizePx:F0} 像素）· 中键拖拽 · Home/F 复位");
    }

    private static int NearestZoomIndex(float z)
    {
        int best = 0;
        float bestErr = float.MaxValue;
        for (int i = 0; i < ZoomSteps.Length; i++)
        {
            float err = MathF.Abs(ZoomSteps[i] - z);
            if (err < bestErr) { bestErr = err; best = i; }
        }
        return best;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // 诊断（FORGEFLOW_CAMDIAG=1）：把**每一个**到达相机的输入事件打出来。
        // 为什么要到这一级：实测「相机缩放会自己从 0.5 漂到 0.2/0.1」，
        // 而在 StepZoom 里打点却抓不到（15 秒跑里一次都没有）——
        // 那就必须往上游找：**到底是哪种事件进来了**。
        if (_camDiag)
            GD.Print($"[camdiag] {(@event as InputEventMouseButton)?.ButtonIndex.ToString() ?? @event.GetType().Name}" +
                     $"{(@event is InputEventMouseButton b ? $" pressed={b.Pressed} pos={b.Position}" : "")}");
        if (!_enabled) return;

        switch (@event)
        {
            case InputEventMouseButton mb:
                // 中键拖拽
                if (mb.ButtonIndex == MouseButton.Middle)
                {
                    _dragging = mb.Pressed;
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (!mb.Pressed) return;

                if (mb.ButtonIndex == MouseButton.WheelUp) { StepZoom(+1); GetViewport().SetInputAsHandled(); }
                else if (mb.ButtonIndex == MouseButton.WheelDown) { StepZoom(-1); GetViewport().SetInputAsHandled(); }
                return;

            case InputEventMouseMotion mm when _dragging:
                // 抓取式平移：把鼠标位移换算成世界位移。
                // ⚠ 必须除以缩放 —— 缩放到 2× 时，屏幕上拖 10px 只对应世界里 5px。
                Position -= mm.Relative / Zoom * DragSensitivity;
                GetViewport().SetInputAsHandled();
                return;

            case InputEventKey { Pressed: true, Echo: false } key:
                switch (key.Keycode)
                {
                    case Key.Home:
                    case Key.F:
                        Position = HomePosition;
                        Zoom = new Vector2(HomeZoom, HomeZoom);
                        _zoomIndex = NearestZoomIndex(HomeZoom);
                        GD.Print($"[camera] 复位到 {HomePosition}（缩放 {HomeZoom}）");
                        GetViewport().SetInputAsHandled();
                        break;
                }
                return;
        }
    }

    /// <summary>滚轮缩放一档，**以光标下的世界点为锚**（那一点在屏幕上不动）。</summary>
    private void StepZoom(int dir)
    {
        int next = Math.Clamp(_zoomIndex + dir, 0, ZoomSteps.Length - 1);
        if (next == _zoomIndex) return;

        // ⚠ **不要用 `InputEvent.Position` 当锚点。** 事件坐标是**滞后**的
        //   （OS 把鼠标移动合并成批、引擎在帧边界才派发；实测同一次移动里与真实光标
        //    差 **170px**）⇒ 锚点取错，滚轮缩放就会「漂」。这里用 `GetGlobalMousePosition()`：
        //   它来自**此刻的真实光标**，而且与下面读的 `Position`/`Zoom` 是同一套画布变换。
        //
        // ⚠ 也**不要**手算 `Position + (光标 − 视口一半)/Zoom`。那个式子里「光标」有歧义：
        //   逻辑视口坐标 与 窗口像素 只在 `stretch == 1`（窗口模式）时碰巧相等，
        //   全屏/不同 DPI 下就偏 —— 而 `Camera2D.Position` 是世界坐标，三者不能混。
        //
        // 下面的推导**完全在世界空间里**（CENTER 锚点下 `世界点 = Position + (光标 − 视口一半)/Zoom`）：
        //   缩放前：anchor = Position + (光标 − 视口一半) / ZoomBefore
        //   缩放后：希望 anchor = Position' + (同一项) / ZoomAfter
        //   由第一式得 (光标 − 视口一半) = (anchor − Position) × ZoomBefore，代入第二式：
        //     Position' = anchor − (anchor − Position) × ZoomBefore / ZoomAfter
        // ⇒ 不需要视口尺寸、也不需要光标的屏幕坐标，因此**不存在选错坐标空间的问题**。
        Vector2 anchor = GetGlobalMousePosition();
        Vector2 before = Position;
        float zBefore = Zoom.X;

        _zoomIndex = next;
        float zAfter = ZoomSteps[_zoomIndex];
        Zoom = new Vector2(zAfter, zAfter);
        Position = anchor - (anchor - before) * (zBefore / zAfter);

        // 诊断：把「锚点」与「缩放后的相机位置」打出来，并给出锚点的**格坐标** ——
        // 人工核对时滚轮一滚，HUD 的「光标格」应当**保持不变**（那正是「以光标为锚点」的定义）。
        if (_camDiag)
            GD.Print($"[camera] StepZoom(dir={dir}) 档位 {_zoomIndex}({zBefore} → {zAfter})  " +
                     $"锚点世界={anchor} 格={ForgeFlow.Sim.FactoryLayout.WorldToGrid(anchor.X, anchor.Y)}  " +
                     $"相机 {before} → {Position}");

        RefreshSubPixelFlag();
    }

    public override void _Process(double delta)
    {
        if (!_enabled) return;

        var dir = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) dir.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) dir.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) dir.Y -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) dir.Y += 1f;

        if (dir == Vector2.Zero) return;

        // 按住 Shift 加速。速度除以缩放 ⇒ 屏幕上看到的移动速度与缩放无关。
        float fast = Input.IsPhysicalKeyPressed(Key.Shift) ? 3f : 1f;
        Position += dir.Normalized() * (PanSpeedPx * fast * (float)delta) / Zoom;
    }
}
