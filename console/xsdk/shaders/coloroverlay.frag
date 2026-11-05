#version 450
// Sprites/Color/Overlay (the HUD's cards), from the game's own program: the
// colour overlaid on the texel, both halves chosen by the texel's red (at
// least one half: 1 - 2(1 - t)(1 - c), else 2tc), premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 t = texture(g_tex, v_uv);
    vec4 c = v_color * v_blend;
    vec3 lo = 2.0 * t.rgb * c.rgb;
    vec3 hi = vec3(1.0) - 2.0 * (vec3(1.0) - t.rgb) * (vec3(1.0) - c.rgb);
    vec3 rgb = mix(lo, hi, step(0.5, t.r));
    float a = t.a * c.a;
    o_color = vec4(rgb * a, a);
}
