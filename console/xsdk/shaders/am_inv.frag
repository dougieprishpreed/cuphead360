#version 450
// The alpha masks' write (Cuphead/Sprites/AlphaMask, AlphaMasked's first pass), from the
// game's own program: the sprite's colour, its alpha inverted (the pass blends only alpha in).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 c = texture(g_tex, v_uv) * v_color * v_blend;
    o_color = vec4(c.rgb, 1.0 - c.a);
}
