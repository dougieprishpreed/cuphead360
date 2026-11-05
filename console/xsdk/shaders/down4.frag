#version 450
// Hidden/FastBlur pass 0 (BlurOptimized's downsample), from the game's own
// programs: the average of four taps around the texel (the source's texel
// size in p2). Drawn replacing the target.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0;
    vec4 p1;
    vec4 p2; // texel size
} F;
layout(location = 0) out vec4 o_color;
void main() {
    vec2 t = F.p2.xy;
    o_color = (texture(g_tex, v_uv + t) + texture(g_tex, v_uv - 0.5 * t)
             + texture(g_tex, v_uv + vec2(0.5, -0.5) * t) + texture(g_tex, v_uv + vec2(-0.5, 0.5) * t)) * 0.25;
}
