#version 450
// Hidden/BlurWithAlpha passes 1-4 (BlurOptimized on Saltbaker's reflection),
// from the game's own programs: seven taps of a Gaussian along p0.xy (uv),
// alpha blurred with the colour. Drawn replacing the target.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // step
    vec4 p1;
    vec4 p2;
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec2 d = F.p0.xy;
    o_color = texture(g_tex, v_uv) * 0.324
            + (texture(g_tex, v_uv - d) + texture(g_tex, v_uv + d)) * 0.232
            + (texture(g_tex, v_uv - 2.0 * d) + texture(g_tex, v_uv + 2.0 * d)) * 0.0855
            + (texture(g_tex, v_uv - 3.0 * d) + texture(g_tex, v_uv + 3.0 * d)) * 0.0205;
}
