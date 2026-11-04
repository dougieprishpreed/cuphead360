#version 450
// Cuphead/Sprites/LightRay (the Ruse of an Ooze's rays), from the game's own
// pixel program: the sprite's red channel masks a shadow texture scrolling
// one way and a sky glow scrolling the other (a quarter as fast), the glow
// over the shadow, premultiplied; t, textureWidth and textureSpeed are the
// LightRay script's material floats.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_mask;
layout(set = 1, binding = 0) uniform sampler2D g_shadow;
layout(set = 2, binding = 0) uniform sampler2D g_glow;
layout(push_constant) uniform Ray {
    layout(offset = 16) vec4 p; // t, textureWidth, textureSpeed, 0
} R;
layout(location = 0) out vec4 o_color;
void main() {
    float m = texture(g_mask, v_uv).r;
    float u = v_uv.x / R.p.y;
    float s = R.p.x * R.p.z;
    // The game's textures repeat; the console's sampler clamps, so wrap here.
    vec4 sh = texture(g_shadow, vec2(fract(u - s), v_uv.y));
    vec4 gl = texture(g_glow, vec2(fract(u + 0.25 * s), v_uv.y));
    vec4 over = vec4(gl.rgb * gl.a, gl.a) + vec4(sh.rgb * sh.a, sh.a) * (1.0 - gl.a);
    vec4 c = over * m * v_color * v_blend;
    o_color = vec4(c.rgb * v_color.a * v_blend.a, c.a);
}
