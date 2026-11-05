#version 450
// Cuphead/Sprites/Honey (the Bee's honey falls), from the game's own program: what was behind it
// (a grab of the target) blurred up and down by _Amount times the texel's alpha, lit by the texel
// (2t over it, a soft light), shown by the texel's alpha; written as it is (Blend One Zero).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(set = 1, binding = 0) uniform sampler2D g_grab;   // the target as it was just before (Unity's GrabPass)
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _Amount
    vec4 p1;
    vec4 p2; // zw: one over the target's size
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 t = texture(g_tex, v_uv);
    vec2 g = vec2(gl_FragCoord.x * F.p2.z, gl_FragCoord.y * F.p2.w);   // xost reads no depth: x and y alone
    float d = F.p0.x * t.a;   // uv units, as Unity adds it to the grab's uvs
    vec3 b = texture(g_grab, g - vec2(0.0, 3.0 * d)).rgb * 0.0205 + texture(g_grab, g - vec2(0.0, 2.0 * d)).rgb * 0.0855
           + texture(g_grab, g - vec2(0.0, d)).rgb * 0.232 + texture(g_grab, g).rgb * 0.324 + texture(g_grab, g + vec2(0.0, d)).rgb * 0.232
           + texture(g_grab, g + vec2(0.0, 2.0 * d)).rgb * 0.0855 + texture(g_grab, g + vec2(0.0, 3.0 * d)).rgb * 0.0205;
    vec3 lit = clamp(2.0 * t.rgb, 0.0, 1.0);
    vec3 l = clamp(2.0 * t.rgb - 1.0, 0.0, 1.0) * (1.0 - b * lit) + b * lit;
    o_color = vec4(mix(b, l, t.a), t.a);
}
