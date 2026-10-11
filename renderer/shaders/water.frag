#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "ocean.glsl"
layout(location = 0) in vec3 worldPosition;
layout(location = 1) in vec2 restXZ;
layout(location = 0) out vec4 outColor;
const float PI = 3.14159265359;

float hash(vec2 p) {
    // Integer cell hashing gives adjacent noise cells exactly the same shared corner values.
    uvec2 cell = uvec2(ivec2(p));
    uint h = cell.x * 0x9e3779b9u ^ cell.y * 0x85ebca6bu;
    h ^= h >> 16; h *= 0x7feb352du;
    h ^= h >> 15; h *= 0x846ca68bu;
    h ^= h >> 16;
    return float(h >> 8) * (1.0 / 16777216.0);
}
vec3 noise_gradient(vec2 p) {
    vec2 i = floor(p), f = p - i;
    vec2 blend = f * f * (3.0 - 2.0 * f), derivative = 6.0 * f * (1.0 - f);
    float a = hash(i), b = hash(i + vec2(1, 0)), c = hash(i + vec2(0, 1)), d = hash(i + vec2(1, 1));
    return vec3(derivative * vec2(mix(b-a, d-c, blend.y), mix(c-a, d-b, blend.x)),
        mix(mix(a, b, blend.x), mix(c, d, blend.x), blend.y));
}
float value_noise(vec2 p) { return noise_gradient(p).z; }

void main() {
    float time = ocean.params.x;
    vec3 cameraPos = scene.cameraPosition.xyz;
    float range = length(cameraPos - worldPosition);
    vec3 v = normalize(cameraPos - worldPosition);
    // Pixel footprint on the sea: waves shorter than a few pixels are faded out of the normal and their slope
    // energy moves into roughness instead, so the distance broadens the sun glitter rather than shimmering.
    vec2 pixelX = dFdx(restXZ), pixelY = dFdy(restXZ);
    float footprint = length(abs(pixelX) + abs(pixelY));
    float hx = 0.0, hz = 0.0, dxx = 0.0, dzz = 0.0, dxz = 0.0, height = 0.0, unresolved = 0.0;
    vec2 horizontal = vec2(0);
    float shadedHeight = 0.0;
    for (int i = 0; i < OCEAN_WAVES; ++i) {
        vec4 w = oceanWaves[i];
        float ak = w.w * w.z;
        // Filter along each wave's direction; a long depth footprint must not erase resolved cross waves.
        float waveFootprint = length(vec2(dot(w.xy, pixelX), dot(w.xy, pixelY)));
        float resolve = smoothstep(waveFootprint * 2.0, waveFootprint * 4.0, 6.2831853 / w.z);
        unresolved += ak * ak * (1.0 - resolve);
        if (resolve <= 0.0) continue;
        float f = ocean_phase(i, restXZ, time), s = sin(f), c = cos(f);
        ak *= resolve;
        hx -= ak * w.x * s; hz -= ak * w.y * s;
        dxx -= OCEAN_CHOP * ak * w.x * w.x * c; dzz -= OCEAN_CHOP * ak * w.y * w.y * c; dxz -= OCEAN_CHOP * ak * w.x * w.y * c;
        horizontal -= OCEAN_CHOP * w.xy * w.w * resolve * s;
        shadedHeight += w.w * resolve * c;
        if (i < OCEAN_DISPLACED) height += w.w * resolve * c;
    }
    // Irregular surface detail between the analytic swells, filtered before it becomes sub-pixel noise.
    vec2 detailXZ = restXZ - vec2(.8, .5) * time;
    vec3 broad = noise_gradient(detailXZ * .014 + vec2(17, 43));
    vec3 small = noise_gradient(detailXZ * .05 + vec2(61, 7));
    float broadWeight = 1.0 - smoothstep(.12, .35, footprint * .014);
    float smallWeight = 1.0 - smoothstep(.12, .35, footprint * .05);
    hx += broad.x * .014 * 4.5 * broadWeight + small.x * .05 * 1.3 * smallWeight;
    hz += broad.y * .014 * 4.5 * broadWeight + small.y * .05 * 1.3 * smallWeight;
    float detailHeight = (broad.z - .5) * 4.5 * broadWeight + (small.z - .5) * 1.3 * smallWeight;
    height += detailHeight;
    shadedHeight += detailHeight;
    unresolved += .003 * (2.0 - broadWeight - smallWeight);
    vec3 group = ocean_group(restXZ, time);
    hx = hx * group.z + shadedHeight * group.x;
    hz = hz * group.z + shadedHeight * group.y;
    dxx = dxx * group.z + horizontal.x * group.x;
    dzz = dzz * group.z + horizontal.y * group.y;
    float dzx = dxz * group.z + horizontal.y * group.x;
    dxz = dxz * group.z + horizontal.x * group.y;
    height *= group.z;
    unresolved *= group.z * group.z;
    float jacobian = (1.0 + dxx) * (1.0 + dzz) - dxz * dzx;
    // Cross both displaced tangents, including the mixed derivative of crossing waves.
    vec3 n = normalize(vec3(dzx * hz - (1.0 + dzz) * hx, max(jacobian, .25), dxz * hx - (1.0 + dxx) * hz));

    vec3 l = normalize(scene.sunlightDirection.xyz);
    vec3 sunColor = vec3(1.0, .82, .6) * scene.sunlightColor.rgb;
    float nv = max(dot(n, v), 1e-3);
    float fresnel = .02 + .98 * pow(1.0 - nv, 5.0);
    vec3 r = reflect(-v, n);
    r.y = abs(r.y);
    vec3 reflection = sky_color(r, l);

    // Water body plus FFT-Ocean's subsurface term: light through wave crests seen against the sun.
    // Elevated wave faces scatter more light than troughs; this remains visible from above, away from sun glitter.
    float faceLight = smoothstep(-2.5, 3.5, height) * (.3 + .7 * max(dot(n, l), 0.0));
    vec3 body = mix(vec3(.002, .012, .028), vec3(.008, .05, .073), faceLight);
    vec3 h = normalize(-n + l);
    float through = pow(clamp(dot(v, -h), 0.0, 1.0), 5.0);
    float crest = clamp(height * .25, 0.0, 1.0);
    body += vec3(.006, .07, .065) * through * crest * scene.sunlightColor.r * .5;

    // Sun glitter: GGX whose roughness absorbs the unresolved slopes.
    vec3 halfway = normalize(v + l);
    float nh = max(dot(n, halfway), 0.0), nl = max(dot(n, l), 0.0);
    // GGX alpha^2 ~ mean-square slope of the waves below the pixel (half the summed (ak)^2).
    float a2 = clamp(.0012 + unresolved * .5, .0012, .08);
    float denom = nh * nh * (a2 - 1.0) + 1.0;
    float specular = a2 / (PI * denom * denom) * (.02 + .98 * pow(1.0 - max(dot(v, halfway), 0.0), 5.0)) / (4.0 * nv + 1e-3);
    vec3 color = mix(body, reflection, fresnel) + sunColor * specular * nl;

    // Break up compressed crests into metre-scale whitecaps and larger patches that survive an aerial view.
    vec2 foamXZ = restXZ - vec2(.94, .34) * time * 1.2;
    float patches = value_noise(foamXZ * .025) * .65 + value_noise(foamXZ * .075) * .35;
    float bubbles = mix(value_noise(foamXZ * vec2(.35, .8)), .5, smoothstep(.35, 2.0, footprint));
    float folding = (1.0 - smoothstep(.62, .98, jacobian)) * smoothstep(-.5, 1.5, height);
    float foam = smoothstep(.08, .5, folding * (.3 + patches) * (.65 + bubbles * .35));
    foam *= mix(1.0, .6, smoothstep(2.0, 12.0, footprint));
    vec3 foamColor = vec3(.92, .94, .96) * (.12 + .3 * max(dot(n, l), .3) * scene.sunlightColor.r) + sky_color(vec3(0, 1, 0), l) * .25;
    color = mix(color, foamColor, foam * .95);

    // Aerial perspective toward the horizon.
    vec3 haze = sky_color(normalize(vec3(-v.x, .02, -v.z)), l);
    color = mix(color, haze, 1.0 - exp(-range / 22000.0));
    outColor = vec4(color, 1.0);
}
