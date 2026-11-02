#version 450
// Screen pixels (top-left origin, the host flips Unity's y) to Vulkan clip
// space; the texture coordinate, the vertex colour and the draw's blend
// colour (the renderer colour of the screen/multiply/overlay sprite shaders).
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec4 a_color;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in vec4 a_blend;

layout(push_constant) uniform Target {
    vec4 viewport; // 2 / width, 2 / height, -1, -1
} T;

layout(location = 0) out vec4 v_color;
layout(location = 1) out vec2 v_uv;
layout(location = 2) out vec4 v_blend;

void main() {
    gl_Position = vec4(a_pos * T.viewport.xy + T.viewport.zw, 0.0, 1.0);
    v_color = a_color;
    v_uv = a_uv;
    v_blend = a_blend;
}
