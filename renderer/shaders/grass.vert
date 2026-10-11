#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "meadow.glsl"
layout(location = 0) out vec3 worldPosition;
layout(location = 1) out vec3 bladeNormal;
layout(location = 2) out float along;      // 0 at the root, 1 at the tip
layout(location = 3) out float side;       // -1 left edge, +1 right edge
layout(location = 4) flat out vec3 widthDir;
layout(location = 5) flat out vec4 blade;  // x = tint, y = dry, z = gust, w = sun on the hillside
layout(location = 6) flat out float flower; // 0 = grass or stem, 1-4 = flower head species
layout(location = 7) out float petal;       // flower head: 0 at the centre, 1 at the rim

// Blades are generated from gl_VertexIndex, one draw per distance band (gl_InstanceIndex = band); no buffers.
// Each band covers a square of world-aligned cells around the camera, one blade per cell, so a cell always grows the
// same blade wherever the camera is. Keep in sync with GrassBands in vk_engine.cpp (spacing, outer radius, vertices).
// Band 3 is wildflowers: a stem triangle and a 24-triangle head, grown only inside flower patches.
const float SPACING[4] = float[](.042, .1, .24, .35);
const float OUTER[4] = float[](14.0, 40.0, 90.0, 35.0);
const int VERTS[4] = int[](15, 9, 3, 75);   // triangles: 2 quads + tip, 1 quad + tip, tip only, stem + head
const float FADE = 3.0;

void cull() { gl_Position = vec4(2, 2, 2, 1); worldPosition = vec3(0); bladeNormal = vec3(0, 1, 0); along = 0; side = 0; petal = 0; }

void main() {
    int band = gl_InstanceIndex;
    float spacing = SPACING[band], outer = OUTER[band];
    int verts = VERTS[band], half_ = int(ceil(outer / spacing)), sideCount = 2 * half_;
    int index = gl_VertexIndex / verts, corner = gl_VertexIndex % verts;
    ivec2 cell = ivec2(floor(ocean.params.yz / spacing)) + ivec2(index % sideCount, index / sideCount) - half_;
    uint h = meadow_hash_u(cell * 3 + band * 7919);
    vec4 r = vec4(uvec4(h, h * 0x9e3779b9u, h * 0x85ebca6bu, h * 0xc2b2ae35u) >> 8) * (1.0 / 16777216.0);
    vec2 rootXZ = (vec2(cell) + r.xy) * spacing;
    vec2 rel = rootXZ - ocean.params.yz;
    float d = length(rel);

    // Band hand-off: blades shrink to nothing across the outer FADE metres while the next band grows in.
    bool isFlower = band == 3;
    float inner = band == 1 || band == 2 ? OUTER[band - 1] - FADE : -1.0;
    float grow = (1.0 - smoothstep(outer - (band >= 2 ? 15.0 : FADE), outer, d)) * smoothstep(inner, inner + FADE, d);
    if (grow <= .01) { cull(); return; }

    float dry = meadow_dry(rootXZ);
    int species = 0;
    if (isFlower) {
        // Flowers gather in patches, thinning out where the grass is dry; each patch is mostly one species.
        float patch_ = smoothstep(.45, .8, meadow_noise(rootXZ / 14.0 + vec2(71, 3)).z) * (1.0 - .7 * dry);
        if (fract(r.w * 91.7) > patch_) { cull(); return; }
        species = int(meadow_noise(rootXZ / 30.0 + vec2(13, 57)).z * 3.999);
        if (fract(r.x * 53.1) < .2) species = (species + 1) % 4;
    }
    float height = (isFlower ? mix(.6, 1.05, r.z) : mix(.42, .95, r.z) * mix(1.05, .7, dry)) * grow;
    vec3 terrain = meadow_terrain(rootXZ, 0.0);
    vec3 root = vec3(rel.x, terrain.z - ocean.params.w - .04, rel.y);

    // Frustum test on the blade's bounding sphere before any further work.
    vec4 clip = scene.viewproj * vec4(root + vec3(0, height * .5, 0), 1.0);
    float radius = height * 2.0 + .2;
    if (clip.w < -radius || any(greaterThan(abs(clip.xy), vec2(clip.w + radius * 2.0)))) { cull(); return; }

    // Bend: a resting lean in a random direction plus the wind, with sway and (near band) tip flutter.
    float time = ocean.params.x, strength = length(ocean.wind.xy);
    vec2 windDir = strength > 1e-4 ? ocean.wind.xy / strength : vec2(1, 0);
    float gust = meadow_gust(rootXZ, time);
    float stiffness = mix(.7, 1.4, r.w) * (isFlower ? 1.6 : 1.0);
    float sway = sin(time * mix(1.4, 2.6, r.x) + r.y * 6.2831853) * (.06 + .1 * gust) * strength;
    float windAngle = strength * (.18 + gust * ocean.wind.z * 1.05) / stiffness + sway;
    float leanAngle = .18 + .5 * fract(r.x * 7.31), leanAzimuth = r.y * 6.2831853;
    vec2 bend = vec2(cos(leanAzimuth), sin(leanAzimuth)) * leanAngle + windDir * windAngle;
    float phi = min(length(bend), 1.3);
    vec2 bendDir = phi > 1e-4 ? bend / length(bend) : vec2(1, 0);

    // Quadratic Bezier from the root: control point straight up, tip on a circle of radius `height`, so the blade arcs
    // over rather than stretching as the wind bends it.
    vec3 p0 = root, p1 = root + vec3(0, height * .62, 0);
    vec3 p2 = root + height * vec3(bendDir.x * sin(phi), cos(phi), bendDir.y * sin(phi));

    float facing = fract(r.z * 13.7 + r.w * 3.1) * 6.2831853;
    vec3 wdir = vec3(cos(facing), 0, sin(facing));
    // Wider blades further out keep at least a pixel of coverage instead of sparkling.
    float width = isFlower ? .004 : .0065 * pow(spacing / SPACING[0], 1.3) * mix(.7, 1.3, r.w);
    width = max(width, d * ocean.wind.w * 1.1);
    flower = 0.0;
    petal = 0.0;
    vec3 l = normalize(scene.sunlightDirection.xyz);
    blade = vec4(r.x, dry, gust * ocean.wind.z * min(strength, 1.0),
        smoothstep(-.12, .2, dot(meadow_normal(terrain), l)) * meadow_cloud_shadow(rootXZ, terrain.z, l));

    if (isFlower && corner >= 3) {
        // Head: a 24-triangle fan whose rim follows rounded petals, facing the sky and tilted toward the viewer.
        // Daisy, buttercup, cornflower, poppy: radius (m), petal count, how far the gaps between petals cut in.
        const float RADIUS[4] = float[](.034, .022, .028, .045), PETALS[4] = float[](12, 5, 8, 4), NOTCH[4] = float[](.72, .55, .45, .8);
        int k = (corner - 3) / 3, vv = (corner - 3) % 3, rim = k + vv - 1;
        vec3 toCamera = normalize(vec3(-p2.x, 0, -p2.z) + vec3(1e-4, 0, 0));
        vec3 hn = normalize(vec3(0, 1, 0) + toCamera * .65);
        vec3 ax = normalize(cross(hn, toCamera)), ay = cross(ax, hn);
        float radius = max(RADIUS[species] * mix(.8, 1.25, r.x), d * ocean.wind.w * 1.6);
        float angle = float(rim) / 24.0 * 6.2831853 + r.y * 6.2831853;
        float reach = vv == 0 ? 0.0 : mix(NOTCH[species], 1.0, sqrt(abs(cos(angle * PETALS[species] * .5))));
        vec3 pos = p2 + (ax * cos(angle) + ay * sin(angle)) * radius * reach + hn * .004 * reach;
        worldPosition = pos;
        bladeNormal = hn;
        along = 1.0;
        side = 0.0;
        widthDir = ax;
        flower = float(species + 1);
        petal = vv == 0 ? 0.0 : 1.0;
        gl_Position = scene.viewproj * vec4(pos, 1.0);
        return;
    }

    // Corner -> (level, edge). Levels 0..quads are quad rows; the last triangle closes at the tip (a flower stem is
    // just that tip triangle).
    int quads = isFlower ? 0 : (verts / 3 - 1) / 2, tri = corner / 3, v = corner % 3;
    int level; float edge;
    if (tri < 2 * quads) {
        int q = tri / 2;
        const ivec2 a[3] = ivec2[](ivec2(0, -1), ivec2(0, 1), ivec2(1, -1));
        const ivec2 b[3] = ivec2[](ivec2(1, -1), ivec2(0, 1), ivec2(1, 1));
        ivec2 c = (tri % 2 == 0) ? a[v] : b[v];
        level = q + c.x; edge = float(c.y);
    } else {
        level = v == 2 ? quads + 1 : quads; edge = v == 2 ? 0.0 : (v == 0 ? -1.0 : 1.0);
    }
    float t = float(level) / float(quads + 1);
    float u = 1.0 - t;
    vec3 pos = u * u * p0 + 2.0 * u * t * p1 + t * t * p2;
    vec3 tangent = normalize(2.0 * u * (p1 - p0) + 2.0 * t * (p2 - p1));
    // Taper to the tip, but never below about a pixel: thinner strips break into dotted lines.
    pos += wdir * edge * .5 * max(width * (1.0 - pow(t, 1.4)), d * ocean.wind.w * 1.4);
    if (band == 0) pos += wdir * sin(time * mix(9.0, 15.0, r.x) + r.y * 40.0) * .012 * t * t * strength * (.3 + gust);

    worldPosition = pos;
    bladeNormal = normalize(cross(wdir, tangent));
    along = t;
    side = edge;
    widthDir = wdir;
    gl_Position = scene.viewproj * vec4(pos, 1.0);
}
