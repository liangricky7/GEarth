#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "ocean.glsl"
layout(location = 0) out vec3 worldPosition;
layout(location = 1) out vec2 restXZ;
// Concentric rings around the camera, generated from gl_VertexIndex (no vertex buffer): fine spacing under
// the camera, growing geometrically to the horizon. The surface is evaluated at world xz, so it stays put
// while the grid travels with the camera.
const int RINGS = 290;
const int SEGMENTS = 360;
void main() {
    int quad = gl_VertexIndex / 6, corner = gl_VertexIndex % 6;
    const ivec2 offsets[6] = ivec2[](ivec2(0, 0), ivec2(1, 0), ivec2(1, 1), ivec2(0, 0), ivec2(1, 1), ivec2(0, 1));
    int ring = quad / SEGMENTS + offsets[corner].x;
    int segment = quad % SEGMENTS + offsets[corner].y;
    float radius = 4.0 * (exp(.032 * float(ring)) - 1.0);
    float angle = float(segment) / float(SEGMENTS) * 6.2831853;
    vec2 offset = radius * vec2(cos(angle), sin(angle)), xz = ocean.params.yz + offset;
    // The denser rings retain swell geometry at the kilometre ranges visible through the plane windscreen.
    float cellWidth = (radius + 4.0) * .032;
    vec3 p = vec3(offset.x, -ocean.params.w, offset.y) + ocean_displacement(xz, ocean.params.x, cellWidth * 2.0);
    worldPosition = p; // camera-relative
    restXZ = xz;
    gl_Position = scene.viewproj * vec4(p, 1.0);
}
