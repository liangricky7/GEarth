#ifndef MATERIAL_SET
#define MATERIAL_SET 1
#endif
layout(set = MATERIAL_SET, binding = 0, std140) uniform MaterialData {
    vec4 colorFactors;
    vec4 metal_rough_factors;
    vec4 uvTransform;
    vec4 alphaMask;
    vec4 emission;
    vec4 materialFlags;
    vec4 extra[10];
} material;
layout(set = MATERIAL_SET, binding = 1) uniform sampler2D colorTex;
layout(set = MATERIAL_SET, binding = 2) uniform sampler2D metalRoughTex;
layout(set = MATERIAL_SET, binding = 3) uniform sampler2D normalTex;
