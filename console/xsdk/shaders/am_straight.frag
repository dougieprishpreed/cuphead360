#version 450
// Cuphead/Sprites/AlphaMasked's second pass, from the game's own program: the sprite's
// colour, not premultiplied (the pass blends it in by the target's alpha).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    o_color = texture(g_tex, v_uv) * v_color * v_blend;
}
