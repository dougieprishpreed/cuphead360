#version 450
// Cuphead/Sprites/AlphaMaskedNonDestructiveReversedGlyphs' first pass, from the game's own
// program: the sprite's alpha in every channel.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    float a = texture(g_tex, v_uv).a * (v_color * v_blend).a;
    o_color = vec4(a);
}
