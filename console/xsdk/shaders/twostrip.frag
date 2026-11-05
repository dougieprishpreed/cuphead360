#version 450
// Hidden/BlurGamma pass 2 (the two-strip filter), from the game's own
// program: the seven-tap blur along p0.xy, then the colour as two-strip film
// (red kept, green from red and the green-blue average, blue that average,
// widened by 1.25), to the power p0.z. Drawn replacing the target.
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
    float gb = (b.g + b.b) * 0.5;
    float g = (1.0 - 0.67 * (1.0 - gb)) * (1.0 - 0.34 * (1.0 - b.r));
    vec3 c = vec3(b.r, g, gb) * 1.25 - 0.25;
    o_color = vec4(exp2(log2(max(c, vec3(1e-6))) * F.p0.z), m.a);
}
