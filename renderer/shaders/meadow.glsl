// Shared by terrain.* and grass.* (the meadow view). Includes ocean.glsl for the push constants and the sky.
// World units are metres, y up; geometry is camera-relative, patterns use absolute x/z (ocean.params.yz = camera x/z).
#include "ocean.glsl"

// Integer lattice hash (as water.frag); meadow_terrain in vk_engine.cpp mirrors this file's terrain exactly.
uint meadow_hash_u(ivec2 cell) {
    uvec2 c = uvec2(cell);
    uint h = c.x * 0x9e3779b9u ^ c.y * 0x85ebca6bu;
    h ^= h >> 16; h *= 0x7feb352du;
    h ^= h >> 15; h *= 0x846ca68bu;
    h ^= h >> 16;
    return h;
}
float meadow_hash(ivec2 cell) { return float(meadow_hash_u(cell) >> 8) * (1.0 / 16777216.0); }
// Value noise: xy = gradient, z = value in [0,1].
vec3 meadow_noise(vec2 p) {
    vec2 i = floor(p), f = p - i;
    ivec2 c = ivec2(i);
    vec2 u = f * f * (3.0 - 2.0 * f), du = 6.0 * f * (1.0 - f);
    float a = meadow_hash(c), b = meadow_hash(c + ivec2(1, 0)), d = meadow_hash(c + ivec2(0, 1)), e = meadow_hash(c + ivec2(1, 1));
    return vec3(du * vec2(mix(b - a, e - d, u.y), mix(d - a, e - b, u.x)), mix(mix(a, b, u.x), mix(d, e, u.x), u.y));
}

// Rolling hills: five octaves from 620 m down to 34 m. Octaves shorter than ~4 footprints fade out (0 = full detail).
// Returns xy = height gradient, z = height (metres).
const int MEADOW_OCTAVES = 5;
vec3 meadow_terrain(vec2 xz, float footprint) {
    vec3 r = vec3(0.0);
    float amp = 22.0, freq = 1.0 / 620.0;
    for (int i = 0; i < MEADOW_OCTAVES; ++i) {
        vec3 n = meadow_noise(xz * freq + vec2(37.1, 11.7) * float(i));
        float keep = 1.0 - smoothstep(.12, .25, footprint * freq);
        r += vec3(n.xy * freq, n.z - .5) * (2.0 * amp * keep);
        amp *= .45; freq *= 2.07;
    }
    return r;
}
vec3 meadow_normal(vec3 terrain) { return normalize(vec3(-terrain.x, 1.0, -terrain.y)); }

// Wind: ocean.wind.xy = direction * strength (0-1.5), z = gustiness (0-1). Gust fronts are noise scrolled downwind,
// stretched across the wind so they read as long bands rolling over the field. Returns 0 (calm) to 1 (gust).
float meadow_gust(vec2 xz, float time) {
    float strength = length(ocean.wind.xy);
    vec2 dir = strength > 1e-4 ? ocean.wind.xy / strength : vec2(1, 0), across = vec2(-dir.y, dir.x);
    vec2 p = xz - dir * (3.5 + 3.0 * strength) * time;
    vec2 q = vec2(dot(p, dir), dot(p, across));
    float n = meadow_noise(q / vec2(16.0, 34.0)).z * .65 + meadow_noise(q / vec2(5.5, 10.0) + vec2(31, 17)).z * .35;
    return smoothstep(.38, .82, n);
}
// Fair-weather clouds on a flat layer 1.8 km up, drifting slowly: density 0-1 at a point on the layer.
const float CLOUD_HEIGHT = 1800.0;
float meadow_cloud(vec2 xz) {
    vec2 q = (xz - vec2(6.0, 3.5) * ocean.params.x) / 2400.0;
    float f = 0.0, a = .5;
    for (int i = 0; i < 5; ++i) { f += a * meadow_noise(q).z; q = q * 2.03 + vec2(17.3, 9.1); a *= .5; }
    return smoothstep(.5, .74, f);
}
// The cloud layer seen along `dir` from a point at xz and height h, composited over `color`. Lit from the sun side
// with a bright rim toward the sun, grey-violet undersides; faded toward the horizon where it is too foreshortened.
vec3 sky_clouds(vec3 color, vec3 dir, vec3 sun, vec2 xz, float h) {
    if (dir.y <= .015) return color;
    vec2 hit = xz + dir.xz / dir.y * (CLOUD_HEIGHT - h);
    float density = meadow_cloud(hit);
    float lit = exp(-2.2 * meadow_cloud(hit + sun.xz * 350.0));
    float forward = pow(max(dot(dir, sun), 0.0), 6.0);
    vec3 sunColor = vec3(1.0, .7, .45) * scene.sunlightColor.rgb;
    vec3 cloud = sunColor * lit * (.16 + .9 * forward) + sky_color(vec3(0, 1, 0), sun) * .9 + vec3(.05, .045, .06);
    return mix(color, cloud, density * smoothstep(.015, .12, dir.y) * .92);
}
// Sunlight left after the cloud layer, for ground at xz and height h (cloud shadows sweep across the field).
float meadow_cloud_shadow(vec2 xz, float h, vec3 l) {
    return 1.0 - .7 * meadow_cloud(xz + l.xz / max(l.y, .05) * (CLOUD_HEIGHT - h));
}
// Large dry/lush patches and their blade heights.
float meadow_dry(vec2 xz) { return smoothstep(.35, .8, meadow_noise(xz / 46.0 + vec2(5, 9)).z * .75 + meadow_noise(xz / 9.0).z * .25); }

// Blade albedo (linear) from root (t=0) to tip (t=1).
vec3 grass_color(float t, float dry, float tint) {
    vec3 root = vec3(.012, .022, .005), mid = vec3(.035, .085, .014), tip = vec3(.085, .17, .03);
    vec3 dryMid = vec3(.11, .09, .03), dryTip = vec3(.36, .27, .1);
    vec3 m = mix(mid, dryMid, dry), tp = mix(tip, dryTip, dry);
    vec3 c = t < .45 ? mix(root, m, t / .45) : mix(m, tp, (t - .45) / .55);
    return c * (.8 + .4 * tint) * mix(vec3(1), vec3(1.15, 1.0, .75), tint * .4);
}

// Grass lighting: wrapped sun diffuse, translucency when backlit, a weak sheen, sky ambient. `sunMask` darkens slopes
// facing away from the sun; `t` is height in the canopy (low = occluded by neighbours).
vec3 meadow_shade(vec3 albedo, vec3 n, vec3 v, vec3 l, float t, float sunMask) {
    vec3 sun = vec3(1.0, .74, .48) * scene.sunlightColor.rgb;
    float nl = dot(n, l);
    float canopy = mix(.18, 1.0, smoothstep(0.0, 1.0, t));
    float diffuse = clamp((nl + .45) / 1.45, 0.0, 1.0);
    float back = pow(clamp(dot(-v, l), 0.0, 1.0), 5.0);
    vec3 translucent = albedo * vec3(1.3, 1.5, .6) * (back * 1.3 + clamp(-nl, 0.0, 1.0) * .35) * t;
    float spec = pow(clamp(dot(n, normalize(v + l)), 0.0, 1.0), 28.0) * .08 * t;
    vec3 sky = sky_color(vec3(0, 1, 0), l) * .9 + sky_color(normalize(vec3(-l.x, .15, -l.z)), l) * .5;
    return sun * sunMask * canopy * (albedo * diffuse + translucent + spec) + sky * albedo * mix(.12, 1.0, t);
}
vec3 meadow_haze(vec3 color, vec3 v, vec3 l, float range) {
    return mix(color, sky_color(normalize(vec3(-v.x, .03, -v.z)), l), 1.0 - exp(-range / 4500.0));
}
