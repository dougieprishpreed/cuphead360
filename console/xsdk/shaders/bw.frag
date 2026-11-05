#version 450
// Hidden/BlurGamma pass 3 (the black-and-white filter), from the game's own
// program: the seven-tap blur along p0.xy, the colour to the power p0.z,
// then grey (the greater of the plain and the luminance average, widened by
// 1.15) to the power 0.9 p0.z. Drawn replacing the target.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // step, power
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
    vec3 c = exp2(log2(max(b, vec3(1e-6))) * F.p0.z);
    float x = max(dot(c, vec3(0.2125, 0.7154, 0.0721)), dot(c, vec3(0.333))) * 1.15 - 0.1;
    float y = exp2(log2(max(x, 1e-6)) * F.p0.z * 0.9);
    o_color = vec4(y, y, y, m.a);
}
