#version 450
// Sprites/BishopDarkness, from the game's own pixel program: black, as
// opaque as the texel's red times _Level. The texture is what the Bishop's
// background camera drew: white but for the glow masks around his candles,
// the Bishop and the players. Premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _Level
    vec4 p1;
    vec4 p2;
} F;
layout(location = 0) out vec4 o_color;
void main() {
    o_color = vec4(0.0, 0.0, 0.0, texture(g_tex, v_uv).r * F.p0.x);
}
