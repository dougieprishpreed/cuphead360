#version 450
// Sprites/KnightGlow (the Knight's lightning), from the game's own pixel
// program. The texture is what the glow camera drew (the knight alone).
// Inside his silhouette (alpha over 0.1) five taps walk toward the light,
// _LightingVector texels a step, leaning out across the screen from its
// middle; each tap outside the silhouette adds _OutlineIncreaseFactor of
// _OutlineColor (up to its alpha), and the rest of him is darkened by
// _DimFactor. Outside the silhouette the texel as it is. Premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _OutlineColor
    vec4 p1;                     // _DimFactor, _OutlineIncreaseFactor, _LightingVector
    vec4 p2;                     // texel size
} F;
layout(location = 0) out vec4 o_color;

float outside(vec2 uv) {
    return 1.0 - step(0.1, texture(g_tex, uv).a);
}

void main() {
    vec4 s = texture(g_tex, v_uv);
    vec2 d = vec2(clamp(v_uv.x * 4.0 - 2.0, -0.5, 0.5) * F.p2.x * F.p1.z, F.p2.y * F.p1.w);
    float n = outside(v_uv + d) + outside(v_uv + 2.0 * d) + outside(v_uv + 3.0 * d) + outside(v_uv + 4.0 * d) + outside(v_uv + 5.0 * d);
    float k = 1.0 - clamp(n * F.p1.y, 0.0, F.p0.a);
    vec4 lit = vec4(F.p0.rgb * (1.0 - k), 1.0 + k * (F.p1.x - 1.0));
    o_color = mix(s, lit, step(0.1, s.a));
}
