#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "meadow.glsl"
layout(location = 0) in vec3 worldPosition;
layout(location = 1) in vec3 bladeNormal;
layout(location = 2) in float along;
layout(location = 3) in float side;
layout(location = 4) flat in vec3 widthDir;
layout(location = 5) flat in vec4 blade;
layout(location = 6) flat in float flower;
layout(location = 7) in float petal;
layout(location = 0) out vec4 outColor;
void main() {
    float range = length(worldPosition);
    vec3 v = -worldPosition / range;
    vec3 l = normalize(scene.sunlightDirection.xyz);
    // Two-sided: face the viewer, then round the flat strip across its width like a curled leaf, and lean the
    // normal up toward the tip so the field lights as one canopy.
    vec3 n = gl_FrontFacing ? bladeNormal : -bladeNormal;
    n = normalize(n + widthDir * side * .55 + vec3(0, .45 * along, 0));
    // Gusts tilt blades so their paler faces catch the light: the visible bands that roll across the field.
    vec3 albedo = grass_color(along, blade.y, blade.x) * (1.0 + .45 * blade.z);
    if (flower > .5) {
        // Daisy, buttercup, cornflower, poppy: petal colour with a contrasting centre.
        const vec3 PETALS[4] = vec3[](vec3(.75, .74, .68), vec3(.72, .48, .02), vec3(.03, .07, .62), vec3(.62, .035, .012));
        const vec3 CENTRES[4] = vec3[](vec3(.55, .32, .02), vec3(.6, .4, .02), vec3(.12, .06, .3), vec3(.012, .008, .008));
        int s = int(flower - .5);
        albedo = mix(CENTRES[s], PETALS[s] * mix(.75, 1.0, petal), smoothstep(.22, .34, petal));
    }
    vec3 color = meadow_shade(albedo, n, v, l, along, blade.w);
    outColor = vec4(meadow_haze(color, v, l, range), 1.0);
}
