#version 450
// Sprites/Blend Modes/Screen and Hard Light (p0.x 1): M added (see blendk.frag), its part
// at or above zero. Screen: a c s; Hard Light, by the texel's red: at least one half a (2s - 1),
// else a s. Alpha a (Unity's SrcAlpha blend).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // Hard Light
    vec4 p1;
    vec4 p2;
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 s = texture(g_tex, v_uv);
    vec4 c = v_color * v_blend;
    float a = s.a * c.a;
    vec3 screen = a * c.rgb * s.rgb;
    vec3 hard = a * mix(s.rgb, 2.0 * s.rgb - 1.0, step(0.5, s.r));
    o_color = vec4(max(mix(screen, hard, F.p0.x), vec3(0.0)), a);
}
