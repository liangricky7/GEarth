#version 450
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(location = 0) out vec4 outColor;
layout(push_constant) uniform FilterSettings { vec4 params; vec4 bounds; } push;

// A bounded directional FXAA filter on the display-encoded image.
float luminance(vec3 c) { return dot(c, vec3(.299, .587, .114)); }
vec3 readColor(vec2 p) { return textureLod(sceneColor, clamp(p, push.params.xy * .5, push.bounds.xy), 0).rgb; }
void main() {
    vec2 pixel = push.params.xy;
    vec2 uv = gl_FragCoord.xy * pixel;
    vec3 center = readColor(uv);
    float nw = luminance(readColor(uv + pixel * vec2(-1, -1)));
    float ne = luminance(readColor(uv + pixel * vec2(1, -1)));
    float sw = luminance(readColor(uv + pixel * vec2(-1, 1)));
    float se = luminance(readColor(uv + pixel * vec2(1, 1)));
    float mid = luminance(center);
    float low = min(mid, min(min(nw, ne), min(sw, se)));
    float high = max(mid, max(max(nw, ne), max(sw, se)));
    if (high - low < max(.03125, high * push.params.z)) {
        outColor = vec4(center, 1);
        return;
    }
    vec2 direction = vec2(sw + se - nw - ne, nw + sw - ne - se);
    float regularizer = max((nw + ne + sw + se) / 32.0, 1.0 / 128.0);
    direction = clamp(direction / (min(abs(direction.x), abs(direction.y)) + regularizer), -8.0, 8.0) * pixel;
    vec3 narrow = (readColor(uv - direction / 6.0) + readColor(uv + direction / 6.0)) * .5;
    vec3 wide = narrow * .5 + (readColor(uv - direction * .5) + readColor(uv + direction * .5)) * .25;
    float candidate = luminance(wide);
    outColor = vec4(candidate < low || candidate > high ? narrow : wide, 1);
}
