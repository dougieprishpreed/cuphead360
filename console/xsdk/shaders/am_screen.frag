#version 450
// The non-destructive alpha-masked Screen sprites' second pass, from the game's own
// program: the texel screened with the colour times _Amount, premultiplied by the texel's alpha.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _Amount
    vec4 p1;
    vec4 p2;
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 t = texture(g_tex, v_uv);
    vec4 c = v_color * v_blend;
    vec3 rgb = t.rgb + c.rgb * c.a * F.p0.x * (vec3(1.0) - t.rgb);
    o_color = vec4(rgb * t.a, t.a);
}
