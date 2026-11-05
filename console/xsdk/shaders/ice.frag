#version 450
// Cuphead/Sprites/IceCubes (Mortimer Freeze's ice), from the game's own program: texels over
// _CutoffMax, or with green and blue under their cutoffs, as they are; under _CutoffMin nothing;
// the rest the blurred grab behind them lit by the texel, toward the texel by _BlendValue.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(set = 1, binding = 0) uniform sampler2D g_grab;   // the target as it was just before (Unity's GrabPass)
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _Amount, _CutoffMax, _CutoffMin, _CutoffGreen
    vec4 p1;
    vec4 p2; // zw: one over the target's size; p1 _CutoffBlue, _BlendValue
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 t = texture(g_tex, v_uv);
    float keep = max(step(F.p0.y, t.a) * (1.0 - step(t.a, F.p0.y)), (1.0 - step(F.p0.w, t.g)) * (1.0 - step(F.p1.x, t.b)) * (1.0 - step(t.a, 0.0)));
    float none = (1.0 - keep) * (1.0 - step(F.p0.z, t.a));
    vec2 g = vec2(gl_FragCoord.x * F.p2.z, gl_FragCoord.y * F.p2.w);   // xost reads no depth: x and y alone
    float d = F.p0.x * t.a;   // uv units, as Unity adds it to the grab's uvs
    vec3 b = texture(g_grab, g - vec2(0.0, 3.0 * d)).rgb * 0.0205 + texture(g_grab, g - vec2(0.0, 2.0 * d)).rgb * 0.0855
           + texture(g_grab, g - vec2(0.0, d)).rgb * 0.232 + texture(g_grab, g).rgb * 0.324 + texture(g_grab, g + vec2(0.0, d)).rgb * 0.232
           + texture(g_grab, g + vec2(0.0, 2.0 * d)).rgb * 0.0855 + texture(g_grab, g + vec2(0.0, 3.0 * d)).rgb * 0.0205;
    vec3 lit = clamp(2.0 * t.rgb, 0.0, 1.0);
    vec3 l = clamp(2.0 * t.rgb - 1.0, 0.0, 1.0) * (1.0 - b * lit) + b * lit;
    vec4 o = vec4(mix(l, t.rgb, F.p1.y), 1.0);
    o_color = mix(mix(o, vec4(0.0), none), t, keep);
}
