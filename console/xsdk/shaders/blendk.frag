#version 450
// Sprites/Blend Modes/Screen and Hard Light (p0.x 1), from the game's own programs. Unity
// draws them from a grab of the screen; both come out as the target times K plus M, so this
// pass multiplies the target by K. Screen: 1 - a + a c (1 - s); Hard Light, by the texel's red:
// at least one half 1 - a + 2a (1 - s), else 1 - a.
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
    vec3 screen = 1.0 - a + a * c.rgb * (1.0 - s.rgb);
    vec3 hard = 1.0 - a + step(0.5, s.r) * 2.0 * a * (1.0 - s.rgb);
    o_color = vec4(mix(screen, hard, F.p0.x), 1.0);
}
