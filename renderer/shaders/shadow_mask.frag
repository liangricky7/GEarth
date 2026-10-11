#version 450
#extension GL_GOOGLE_include_directive : require
#define MATERIAL_SET 0
#include "material.glsl"
layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
void main() {
    // color.a carries baked occlusion, not opacity (see mesh.frag).
    if (texture(colorTex, uv).a * material.colorFactors.a < material.alphaMask.x) discard;
}
