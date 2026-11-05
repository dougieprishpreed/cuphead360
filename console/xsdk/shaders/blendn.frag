#version 450
// Sprites/Blend Modes/Hard Light: M's part under zero (see blendm.frag), taken away.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 s = texture(g_tex, v_uv);
    float a = s.a * (v_color * v_blend).a;
    vec3 hard = a * mix(s.rgb, 2.0 * s.rgb - 1.0, step(0.5, s.r));
    o_color = vec4(max(-hard, vec3(0.0)), 0.0);
}
