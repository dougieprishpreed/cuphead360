#version 450
// Cuphead/Sprites/FastBlurSprites (the Bishop's body as he fades, Saltbaker's
// background tornadoes), from the game's own pixel programs. Unity draws all
// five of its passes, each over the last; here they are composited in one
// draw, the same as drawing them in turn:
//   pass 0: the texel, or (by t) four taps around it times the colour;
//   passes 1 and 4: seven taps down the sprite, 2 and 3 across, _BlurAmount
//   texels apart, times the colour; dark pixels (every channel at or under
//   _DullBlacksCutoff) lose _DullBlacksFactor of their alpha; alpha times t.
// t is _BlurAmount / _BlurLerp (1 without _BlurLerp, 0 without blur), so an
// unblurred sprite is its texel alone. Premultiplied.
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec2 v_uv;
layout(location = 2) in vec4 v_blend;
layout(set = 0, binding = 0) uniform sampler2D g_tex;
layout(push_constant) uniform Fx {
    layout(offset = 16) vec4 p0; // _BlurAmount, _BlurLerp, _DullBlacksCutoff, _DullBlacksFactor
    vec4 p1;
    vec4 p2;                     // texel size (the page's, in its uvs)
} F;
layout(location = 0) out vec4 o_color;

const float W0 = 0.324, W1 = 0.232, W2 = 0.0855, W3 = 0.0205;

vec4 blur7(vec2 uv, vec2 d, vec4 c, float t) {
    vec4 b = texture(g_tex, uv) * W0
           + (texture(g_tex, uv - d) + texture(g_tex, uv + d)) * W1
           + (texture(g_tex, uv - 2.0 * d) + texture(g_tex, uv + 2.0 * d)) * W2
           + (texture(g_tex, uv - 3.0 * d) + texture(g_tex, uv + 3.0 * d)) * W3;
    b *= c;
    float low = step(b.r, F.p0.z) * step(b.g, F.p0.z) * step(b.b, F.p0.z);
    float a = b.a * (1.0 - F.p0.w * low) * t;
    return vec4(b.rgb * a, a);
}

void main() {
    vec4 c = v_color * v_blend;
    vec2 tx = F.p2.xy;
    float hasLerp = step(1e-20, F.p0.y);
    float t = hasLerp * clamp(F.p0.x / max(F.p0.y, 1e-20), 0.0, 1.0) + (1.0 - hasLerp) * step(1e-20, F.p0.x);
    vec4 center = texture(g_tex, v_uv);
    vec4 avg = (texture(g_tex, v_uv + tx) + texture(g_tex, v_uv - 0.5 * tx)
              + texture(g_tex, v_uv + vec2(0.5, -0.5) * tx) + texture(g_tex, v_uv + vec2(-0.5, 0.5) * tx)) * 0.25 * c;
    vec4 r = mix(center, avg, t);
    vec4 acc = vec4(r.rgb * r.a, r.a);
    vec4 v = blur7(v_uv, vec2(0.0, tx.y * F.p0.x), c, t);
    vec4 h = blur7(v_uv, vec2(tx.x * F.p0.x, 0.0), c, t);
    acc = v + (1.0 - v.a) * acc;
    acc = h + (1.0 - h.a) * acc;
    acc = h + (1.0 - h.a) * acc;
    acc = v + (1.0 - v.a) * acc;
    o_color = acc;
}
