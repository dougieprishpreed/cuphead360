#version 450
// TextMesh Pro's distance-field text: the atlas alpha around 0.5, the vertex colour.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(location = 0) out vec4 o_color;
void main() {
    float d = texture(g_tex, v_uv).a;
    vec4 c = v_color * v_blend;
    float a = clamp((d - 0.45) * 10.0, 0.0, 1.0) * c.a;
    o_color = vec4(c.rgb * a, a);
}
