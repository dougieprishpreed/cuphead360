#version 450
// Cuphead/SpriteBlendDynamic/Multiply: the texel times the renderer colour.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    vec4 t = texture(g_tex, v_uv);
    float a = t.a * v_color.a * v_blend.a;   // blend.a: a UI item's alpha (1 for sprites)
    o_color = vec4(t.rgb * v_blend.rgb * a, a);
}
