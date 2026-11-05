#version 450
// Cuphead/Sprites/AlphaMaskClearer, from the game's own program: alpha the colour's (the
// pass writes only alpha).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    o_color = vec4(0.0, 0.0, 0.0, (v_color * v_blend).a);
}
