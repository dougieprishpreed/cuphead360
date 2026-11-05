#version 450
// Hidden/Cuphead/ChromaticAberrationFilmGrain, from the game's own programs:
// red, green and blue each from its own offset (the colour fringes), then
// the film overlay (grain, dust, vignette: grey in rgb, alpha) multiplying
// and lighting: rgb * (colour + 1 - alpha). The overlay's uvs go through
// _UV_Transform (its frames are mirrored at random) and repeat. Drawn
// replacing the target.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(set = 1, binding = 0) uniform sampler2D g_overlay;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // red offset, green offset (uv)
    vec4 p1;                     // blue offset, _UV_Transform.xy
    vec4 p2;                     // _UV_Transform.zw
} F;
layout(location = 0) out vec4 o_color;
void main() {
    float r = texture(g_tex, v_uv - F.p0.xy).r;
    float g = texture(g_tex, v_uv - F.p0.zw).g;
    float b = texture(g_tex, v_uv - F.p1.xy).b;
    vec2 ouv = vec2(dot(v_uv, F.p1.zw), dot(v_uv, F.p2.xy));
    vec4 o = texture(g_overlay, fract(ouv));
    o_color = vec4(o.rgb * (vec3(r, g, b) + 1.0 - o.a), 1.0);
}
