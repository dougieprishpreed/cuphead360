#version 450
// Graphics.Blit without a material: the texels as they are (drawn replacing the target).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    o_color = texture(g_tex, v_uv);
}
