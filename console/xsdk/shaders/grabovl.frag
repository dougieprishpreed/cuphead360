#version 450
// Sprites/Blend Modes/Overlay (Sally's lanterns), from the game's own program: the texel and
// what was behind it (a grab of the target), multiplied where the grab's red is over one half and
// screened under it, times the colour; alpha the texel's times the colour's, premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(set = 1, binding = 0) uniform sampler2D g_grab;   // the target as it was just before (Unity's GrabPass)
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // unused
    vec4 p1;
    vec4 p2; // zw: one over the target's size
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 s = texture(g_tex, v_uv);
    vec4 c = v_color * v_blend;
    vec3 g = texture(g_grab, vec2(gl_FragCoord.x * F.p2.z, gl_FragCoord.y * F.p2.w)).rgb;   // xost reads no depth: x and y alone
    vec3 mul = 2.0 * g * s.rgb;
    vec3 scr = vec3(1.0) - 2.0 * (vec3(1.0) - g) * (vec3(1.0) - s.rgb);
    vec3 o = mix(scr, mul, step(0.5, g.r)) * c.rgb;
    float a = s.a * c.a;
    o_color = vec4(o * a, a);
}
