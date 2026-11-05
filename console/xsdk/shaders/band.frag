#version 450
// The game's banded stencil masks (Cuphead/Sprites/StencilMaskMulti and its clearer), from
// its programs: pixels whose alpha is outside (p2.x, p2.y] (each pass its band) discarded; the
// others write the pass's stencil value, no colour.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // unused
    vec4 p1;
    vec4 p2; // the band
} F;
layout(location = 0) out vec4 o_color;
void main() {
    float a = (texture(g_tex, v_uv) * v_color * v_blend).a;
    if (a <= F.p2.x || a > F.p2.y) discard;
    o_color = vec4(0.0);
}
