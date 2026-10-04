shader_type canvas_item;
render_mode blend_mix;

// =============================================================================
// 程序化网格底 —— 铺在 ColorRect 上。
//
// 网格按**世界坐标**对齐（用 MODEL_MATRIX 把顶点换算到世界空间），
// 所以底下的 ColorRect 可以任意大小/位置，格线始终与工厂的格对齐。
//   ⇒ 相机怎么移动、底铺多大，都不影响对齐；也不用给底设「尺寸 = 世界尺寸」。
//
// 为什么用 shader 而不是贴图：
//   不需要任何素材，改格宽只改一个 uniform；也不会因为缩放产生接缝或模糊。
//
// 为什么要有网格：
//   1. 让画面读出「这是建在网格上的工厂」，而不是几个漂浮的色块；
//   2. **它是建造层的地基** —— 之后放置/拆除/自动铺瓦都对齐这个格，
//      先把格画出来，玩家的空间感与后面的一致性才对得上。
// =============================================================================

uniform float cell_size = 32.0;

// 每多少格画一条主格线（0 或负数 = 不画）
uniform float major_every = 8.0;

// ⚠ 透明度不要调回 0.055 / 0.115：那在深灰底上几乎看不见。
// 现在细格线也读得出来，但不会盖住带面。
uniform vec4 minor_color : source_color = vec4(0.72, 0.80, 0.92, 0.13);
uniform vec4 major_color : source_color = vec4(0.78, 0.86, 0.98, 0.26);

uniform float line_width = 1.0;

varying vec2 v_world;

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
}

/// 到最近格线的距离（像素），用于判断是否落在线上。
float dist_to_line(vec2 grid_pos, float step_cells) {
    vec2 f = fract(grid_pos / step_cells);
    vec2 d = min(f, 1.0 - f) * (step_cells * cell_size);
    return min(d.x, d.y);
}

void fragment() {
    vec2 grid_pos = v_world / cell_size;

    float a_minor = (1.0 - step(line_width, dist_to_line(grid_pos, 1.0))) * minor_color.a;

    float a_major = 0.0;
    if (major_every > 0.5) {
        a_major = (1.0 - step(line_width, dist_to_line(grid_pos, major_every))) * major_color.a;
    }

    float a = max(a_minor, a_major);
    vec3 rgb = a_major > a_minor ? major_color.rgb : minor_color.rgb;
    COLOR = vec4(rgb, a);
}
