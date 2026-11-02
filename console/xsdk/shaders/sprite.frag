#version 450
// Sprites and UI: the texel times the vertex colour, premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 c = texture(g_tex, v_uv) * v_color;
    o_color = vec4(c.rgb * c.a, c.a);
}
