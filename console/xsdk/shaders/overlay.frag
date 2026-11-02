#version 450
// Cuphead/SpriteBlendDynamic/Overlay: overlay of the renderer colour on the texel.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 t = texture(g_tex, v_uv);
    vec3 lo = 2.0 * t.rgb * v_blend.rgb;
    vec3 hi = vec3(1.0) - 2.0 * (vec3(1.0) - t.rgb) * (vec3(1.0) - v_blend.rgb);
    vec3 rgb = mix(lo, hi, step(vec3(0.5), t.rgb));
    float a = t.a * v_color.a;
    o_color = vec4(rgb * a, a);
}
