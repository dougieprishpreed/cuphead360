#version 450
// A sprite mask's stencil write: its sprite's pixels with alpha at or above
// the mask's cutoff (the blend alpha) pass; no colour is written.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    float a = texture(g_tex, v_uv).a * v_color.a;
    if (a < v_blend.a) discard;
    o_color = vec4(0.0);
}
