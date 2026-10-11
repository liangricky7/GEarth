#pragma once
#include "vk_types.h"
#include <span>

// Lightweight static GI for carried models (cockpit, ship): per-vertex sky visibility by CPU ray casting against
// the model's own triangles. A vertex deep under the panel or in a footwell sees little sky and goes dark; one by
// the windshield stays bright. Baked once per model (the model never deforms relative to its camera).
//
// triangles: 3 world-space corners per triangle. For each point/normal pair, out receives the cosine-weighted
// fraction of `rays` hemisphere rays that travel `maxDistance` without hitting a triangle (1 = open sky).
void bake_sky_visibility(std::span<const glm::vec3> triangles, std::span<const glm::vec3> points,
    std::span<const glm::vec3> normals, float maxDistance, int rays, std::span<float> out);
void ao_bake_self_test();
