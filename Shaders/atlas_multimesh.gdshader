shader_type canvas_item;
render_mode blend_mix;

// =============================================================================
// 图集 MultiMesh 着色器（带**滚动 UV**、**按轴取样**与**内容居中**）
//
// 逐实例能力都走 MultiMesh 的 custom_data（GLSL 里的 INSTANCE_CUSTOM）：
//   xy = 该实例在图集里的 UV 原点（内容框左上角）
//   z  = **带面滚动相位**（0..1）；**负数表示不滚动**，此时 z 的绝对值是 **Y 向取样缩放**
//   w  = **带面：流向轴**（+1 = 沿 X 滚，-1 = 沿 Y 滚）；**静态精灵：X 向取样缩放**
//
// ⚠ 判「是不是带面」的分界线必须落在 **0**，不能落在 −0.5：
//   静态精灵的 z = −(内容高 / 32)，而物品图标只有 11~14px 高 ⇒ z = −0.34 ~ −0.44 > −0.5。
//   用 −0.5 当界时它们会被**误判成带面** ⇒ 取样缩放被当成 1（整格）⇒ 图标的尺寸与位置全错
//   （症状：物品看着只有一格的四分之一、缩在角落）。两边的约定必须一致：
//   RenderBridge 里判 `scroll >= 0`，这里判 `z >= 0`。
//
// ⚠ 为什么 z 要兼职两件事（这是本项目的一个关键取舍）：
//   custom_data 只有 4 个 float，而「不滚动」的精灵（机器 / 物品）**用不到 z**，
//   于是把 Y 向取样缩放塞进 z 的绝对值里 —— 约定是「z 为负 ⇒ 静态，且 |z| = Y 缩放」。
//   这样才可能对一个**非方形**的内容框（机械臂 27×41、水塔 39×40、洗矿机 18×51）做
//   精确取样。⚠ **不要**改回「只用一个 uniform uv_scale、取边长 = max(w,h) 的正方形框」：
//   非方形精灵会既偏出中心、又**把右边邻格的像素一起裁进来**（症状：机械臂上出现邻格的图）。
//
// ⚠ 同理，**带面的 w 也兼职**：带面永远是整格取样（两轴缩放恒为 1），所以这一位空出来
//   表示「纹路该沿哪个轴滚」。不区分轴向的后果是纵向带被**横向平移**：整格瓦片每帧错开
//   p 个格宽，纵向的绿色路轨被推到格子中间、断成一段一段（玩家的原话：「y 方向的轨道有问题」）。
//   实测证据：截图里纵向带的相位是 0.75 ⇒ 瓦片整体横移 24px，与瓦片图案逐像素比对确认。
//
// 为什么要内容居中 / 为什么要按内容框取样：
//   这套美术里机器的内容**不在单元格中心**，而且各类机器大小差很多（18×51 .. 47×31）。
//   映射表由 FactorySim.BuildSpriteLut() 按**实测内容包围盒**生成（tools/measure_art.py 量出来的），
//   所以每台机器都是「自己的内容 → 缩放后正好落在一格内」，不会跨格、不会串图。
//
// 为什么要滚动：
//   这套 sierrassets 美术的传送带是**静态瓦片**（同一行 10 张像素完全相同，SHA1 确认），
//   作者没有画带子动画帧。所以流动感只能靠滚动 UV 表现 —— 相位由模拟按「带子实际走了多远」算出，
//   画面速度与逻辑速度天然一致。
//
// ⚠ 踩过的坑（逐条都是实测出来的）：
//   1. **MultiMesh 实例缓冲的字段顺序是 变换 → custom_data → 颜色**，
//      不是「变换 → 颜色 → custom_data」（后者会全透明：custom_data 的
//      (u,v,0,0) 被当颜色读，alpha=0）。见 Scripts/RenderBridge.cs。
//   2. **不要在这里乘 `COLOR`** —— 走实例颜色通路时片元里读到的 COLOR 会让
//      结果整体透明，且没有任何报错。因此本项目关掉了 per-instance 颜色。
//   3. 材质必须挂在 **MultiMeshInstance2D 节点**上，挂到 mesh 上无效。
//   4. **MultiMeshInstance2D 的 `texture` 属性必须设**，否则整个 draw 被跳过。
//   5. `transform_format` 枚举是 **2D = 0、3D = 1**（容易记反）。
//   6. 像素画要用 filter_nearest；图集导入用 Lossless，不要 VRAM Compressed。
//   7. MultiMesh **没有逐实例 Z 排序**，分层靠节点或实例写入顺序。
//   8. **QuadMesh 的 UV 原点在局部 +Y，而 2D 实例变换把 +Y 映射到屏幕下方** ⇒
//      片元里的 `UV.y` 与纹理的 V 是**反的**，不 `uv.y = 1.0 - uv.y` 就会整片上下镜像。
//      这条最坑的地方是它**不报错、也不影响「能不能画出来」**，只让朝向静默错掉。
//      见 fragment() 里的说明。
// =============================================================================

uniform sampler2D atlas_texture : source_color, filter_nearest, repeat_disable;

// 一个图集单元的 UV 尺寸 = (1/列数, 1/行数)。
// 默认对应 sierrassets automation pack 的 10 列 × 118 行。
uniform vec2 cell_size = vec2(0.1, 0.0084745763);

// 整体染色（默认白 = 不变）。**只给预览层用**：铺带的「预计位置」画的也是图集瓦片
// （不是色块），要能是「可铺淡蓝 / 冲突淡红」就得在着色器里染 ——
// fragment 里 `COLOR = texture(...)` 会把节点的 Modulate 覆盖掉，靠 Modulate 染不上。
// ⚠ 主图集层挂的是**另一个** ShaderMaterial 实例，读不到这里的值（否则整个工厂会跟着变色）。
uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

varying vec2 v_atlas_offset;
varying float v_scroll;      // ≥ 0 才滚动（< 0 = 静态精灵）
varying float v_axis;        // 流向轴：+1 沿 X、−1 沿 Y、**0 = 带面但静止（拐角瓦片）**
varying float v_vertical;    // 1 = 纵向带：纹路沿 Y 滚
varying vec2 v_uv_scale;

void vertex() {
    v_atlas_offset = INSTANCE_CUSTOM.xy;
    float z = INSTANCE_CUSTOM.z;
    float w = INSTANCE_CUSTOM.w;

    // z ≥ 0 ⇒ 带面（要滚动，且 w 是流向轴）；z < 0 ⇒ 静态（|z| = Y 向取样缩放，w = X 向缩放）
    bool belt = z >= 0.0;
    v_scroll = belt ? z : -1.0;
    v_axis = belt ? w : 0.0;
    v_vertical = belt && w < 0.0 ? 1.0 : 0.0;
    v_uv_scale = belt ? vec2(1.0) : vec2(w, -z);
}

void fragment() {
    vec2 uv = UV;

    // ⚠ QuadMesh 的 UV 原点在**局部 +Y**，而 2D 实例变换把局部 +Y 映射到屏幕**下方**
    //   ⇒ 不翻这一下，所有精灵都会**上下镜像**。后果不是「看着怪」而是**朝向反了**：
    //   机器朝向角 θ 经过一次镜像会变成 −θ（R(θ)·M = M·R(−θ)），左右对称的机器因此
    //   看起来比蓝图**少转 180°**（玩家的原话），纵向带的箭头也整片朝上。
    //   证据：把铁甲机/出货机/机械臂的截图与图集逐像素比对，flip_ud 的误差比 identity 低 3~4 倍，
    //   而左右不对称的机械臂上 flip_ud(20.9) 明显优于 rot180(33.9) ⇒ 是镜像不是 180° 旋转。
    uv.y = 1.0 - uv.y;

    if (v_scroll >= 0.0 && abs(v_axis) > 0.5) {
        // 滚动：相位增大 ⇒ 采样点沿流向轴**后移** ⇒ 纹路朝流向走（带子的流动方向）。
        // fract 把采样限制在本格内，不会串到图集别的格。
        //
        // ⚠ **轴为 0 时故意不滚** —— 那是**拐角瓦片**（见 FactorySim 里写 scrollAxis 的地方）。
        //   拐角瓦片的图案是**弧形**的、不是可重复的条纹，滚动它只会让拐角与两侧的直线段
        //   错位（症状：拐弯处「撕裂 / 错开半格」）。宁可让它静止。
        if (v_vertical > 0.5) uv.y = fract(uv.y - v_scroll);
        else uv.x = fract(uv.x - v_scroll);
    }

    uv = uv * (cell_size * v_uv_scale) + v_atlas_offset;
    COLOR = texture(atlas_texture, uv) * tint;
}
