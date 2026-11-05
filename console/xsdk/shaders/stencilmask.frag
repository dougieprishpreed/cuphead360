#version 450
// Cuphead/Sprites/StencilMask, from the game's own program: pixels with alpha under one
// half discarded (the others write the stencil; no colour).
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 c = texture(g_tex, v_uv) * v_color * v_blend;
    if (c.a < 0.5) discard;
    o_color = c;
}
