#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "ocean.glsl"
layout(location = 0) out vec4 outColor;
void main() {
    // Rebuild this pixel's view ray from the inverse view-projection.
    vec2 ndc = gl_FragCoord.xy / ocean.viewport.xy * 2.0 - 1.0;
    vec4 world = ocean.inverseViewProj * vec4(ndc, .5, 1.0);
    vec3 dir = normalize(world.xyz / world.w - scene.cameraPosition.xyz);
    vec3 sun = normalize(scene.sunlightDirection.xyz);
    // Below the horizon (past the end of the water grid) continue the sea's distance haze, not the sky.
    if (dir.y < 0.0) dir = normalize(vec3(dir.x, .02, dir.z));
    outColor = vec4(sky_color(dir, sun) + (dir.y > .019 ? sun_disc(dir, sun) : vec3(0.0)), 1.0);
}
