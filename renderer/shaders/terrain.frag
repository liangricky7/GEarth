#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "meadow.glsl"
layout(location = 0) in vec3 worldPosition;
layout(location = 1) in vec2 restXZ;
layout(location = 0) out vec4 outColor;
// The ground between and beyond the blades. Up close it is the dark floor under the canopy; past the last grass band
// it stands in for the canopy itself, with the same colours, lighting and wind sheen as the blades.
void main() {
    float range = length(worldPosition);
    vec3 v = -worldPosition / range;
    vec3 l = normalize(scene.sunlightDirection.xyz);
    float footprint = length(abs(dFdx(restXZ)) + abs(dFdy(restXZ)));
    vec3 terrain = meadow_terrain(restXZ, footprint);
    vec3 n = meadow_normal(terrain);
    float dry = meadow_dry(restXZ);
    float gust = meadow_gust(restXZ, ocean.params.x) * ocean.wind.z * min(length(ocean.wind.xy), 1.0);
    float canopy = smoothstep(6.0, 80.0, range);
    float t = mix(.15, .6, canopy);
    // Clumps at 3 m and 12 m break up the distant canopy, faded out before they shrink below a pixel.
    float clumps = mix(.5, meadow_noise(restXZ / 3.0).z, 1.0 - smoothstep(.15, .4, footprint / 3.0)) * .5
        + mix(.5, meadow_noise(restXZ / 12.0 + vec2(9, 4)).z, 1.0 - smoothstep(.15, .4, footprint / 12.0)) * .5;
    vec3 albedo = grass_color(t, dry, clumps) * mix(1.0, .55 + .7 * clumps, canopy) * .85 * (1.0 + .45 * gust * canopy);
    // Seen from far off, the canopy faces mostly up: light it as a rough upward surface tilted by the hill.
    vec3 shadeNormal = normalize(mix(n, vec3(0, 1, 0), .3) + vec3(0, .4, 0));
    float sunMask = smoothstep(-.12, .2, dot(n, l)) * meadow_cloud_shadow(restXZ, terrain.z, l);
    vec3 color = meadow_shade(albedo, shadeNormal, v, l, t, sunMask);
    outColor = vec4(meadow_haze(color, v, l, range), 1.0);
}
