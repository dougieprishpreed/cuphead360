#version 450
// Cuphead/Sprites/StencilMaskedMultiReversed, from its programs: the sprite with its alpha at
// most p2.x (each pass its cap, drawn where the stencil holds its band), premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // unused
    vec4 p1;
    vec4 p2; // the alpha cap
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 c = texture(g_tex, v_uv) * v_color * v_blend;
    float a = min(c.a, F.p2.x);
    o_color = vec4(c.rgb * a, a);
}
