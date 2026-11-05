#version 450
// Seven taps of a Gaussian along p0.xy (uv), the alpha the middle tap's:
// Hidden/FastBlur passes 1-4 (BlurOptimized) and Hidden/BlurGamma pass 0
// (and its pass 1 at the default brightness), from the game's own programs.
// Drawn replacing the target.
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
    vec4 m = texture(g_tex, v_uv);
    vec3 b = m.rgb * 0.324
           + (texture(g_tex, v_uv - d).rgb + texture(g_tex, v_uv + d).rgb) * 0.232
           + (texture(g_tex, v_uv - 2.0 * d).rgb + texture(g_tex, v_uv + 2.0 * d).rgb) * 0.0855
           + (texture(g_tex, v_uv - 3.0 * d).rgb + texture(g_tex, v_uv + 3.0 * d).rgb) * 0.0205;
    o_color = vec4(b, m.a);
}
