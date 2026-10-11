// Shared by sky.frag, water.vert and water.frag (the ocean flight scene). World units are metres, y up.
layout(push_constant) uniform OceanConstants {
    mat4 inverseViewProj;
    vec4 params;   // x = time in seconds, y/z = camera x/z (centre of the water grid), w = camera height.
                   // Geometry is camera-relative (camera at the origin); only wave phases use absolute x/z.
    vec4 viewport; // xy = framebuffer size in pixels
    vec4 wind;     // meadow only: xy = wind direction * strength, z = gustiness, w = radians per pixel
} ocean;

// Sky that follows the sun's height: warm golden-hour haze when it is low, clear blue when it is high. Linear HDR.
vec3 sky_color(vec3 dir, vec3 sun) {
    float h = clamp(dir.y, 0.0, 1.0);
    float golden = 1.0 - smoothstep(.12, .45, sun.y);
    vec3 horizon = mix(vec3(.4, .5, .66), vec3(.36, .37, .42), golden);
    vec3 zenith = mix(vec3(.03, .11, .36), vec3(.02, .065, .22), golden);
    vec3 color = mix(horizon, zenith, pow(h, .35));
    float s = max(dot(dir, sun), 0.0);
    color += mix(vec3(1.0, .85, .7) * .5, vec3(1.0, .45, .15), golden) * (pow(s, 4.0) * .5 + pow(s, 32.0) * 1.4) * (1.0 - h * .75);
    return color;
}
vec3 sun_disc(vec3 dir, vec3 sun) {
    return vec3(1.0, .88, .7) * smoothstep(.99975, .99988, dot(dir, sun)) * 80.0;
}

// Waves sampled from a JONSWAP wind-sea spectrum (tools/gen_ocean_waves.py; the spectrum model of
// gasgiant/FFT-Ocean, MIT), summed directly instead of through an FFT.
#include "ocean_waves.glsl"
// Travelling wave groups: xy is the spatial gradient, z is the amplitude multiplier.
vec3 ocean_group(vec2 xz, float time) {
    vec2 a = vec2(.011, .007), b = vec2(-.006, .013);
    float u = dot(a, xz) - time * .20, v = dot(b, xz) - time * .15;
    return vec3(.48 * (a * cos(u) * sin(v) + b * sin(u) * cos(v)), 1.2 + .48 * sin(u) * sin(v));
}
float ocean_phase(int i, vec2 xz, float time) {
    vec4 w = oceanWaves[i];
    return w.z * dot(w.xy, xz) - sqrt(9.81 * w.z) * time + oceanPhases[i];
}
// Choppy displacement of the point resting at xz (FFT-Ocean's convention: height a cos f, horizontal
// -chop a d sin f). Waves shorter than `shortest` metres are dropped so coarse distant vertices don't alias.
vec3 ocean_displacement(vec2 xz, float time, float shortest) {
    vec3 p = vec3(0.0);
    for (int i = 0; i < OCEAN_DISPLACED; ++i) {
        vec4 w = oceanWaves[i];
        float a = w.w * smoothstep(shortest, shortest * 2.0, 6.2831853 / w.z);
        float f = ocean_phase(i, xz, time);
        p += vec3(-OCEAN_CHOP * w.x * a * sin(f), a * cos(f), -OCEAN_CHOP * w.y * a * sin(f));
    }
    return p * ocean_group(xz, time).z;
}
