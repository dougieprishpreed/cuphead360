#version 450
// Cuphead/Sprites/MonochromeNoBlack, from the game's own program: the texel
// toward _MonochromeBase by _Factor (its darkest channel lifting the base),
// its alpha toward its brightness over its neighbourhood's; at the
// silhouette's edge (alpha near one half among it and four diagonal taps)
// the texel as it is. Times the colour, premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _MonochromeBase
    vec4 p1;                     // _Factor
    vec4 p2;                     // texel size (the page's, in its uvs)
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec2 tx = F.p2.xy;
    vec4 c = texture(g_tex, v_uv);
    vec4 m = max(max(texture(g_tex, v_uv + vec2(-tx.x, tx.y)), texture(g_tex, v_uv + vec2(tx.x, -tx.y))),
                 max(texture(g_tex, v_uv - tx), texture(g_tex, v_uv + tx)));
    m = max(m, c);
    float edge = 1.0 - 2.0 * abs(m.a - 0.5);
    float ratio = max(max(c.r, c.g), c.b) / max(max(max(m.r, m.g), m.b), 0.5);
    float lo = min(min(c.r, c.g), c.b);
    vec4 mono = F.p0 + lo * (vec4(1.0) - F.p0);
    vec4 r = mix(c, mono, F.p1.x);
    r.a = mix(r.a, ratio * c.a, F.p1.x);
    vec4 o = mix(r, c, edge) * v_color * v_blend;
    o_color = vec4(o.rgb * o.a, o.a);
}
