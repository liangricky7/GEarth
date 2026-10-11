#version 450
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_buffer_reference : require
#include "scene_data.glsl"
#include "vertex.glsl"
layout(push_constant) uniform DrawConstants { mat4 world; VertexBuffer vertices; } draw;
layout(location = 0) out vec3 worldPosition;
layout(location = 1) out vec3 worldNormal;
layout(location = 2) out vec2 uv;
layout(location = 3) out vec4 color;
layout(location = 4) out vec4 tangent;
void main() {
    Vertex v = draw.vertices.vertices[gl_VertexIndex];
    vec4 p = draw.world * vec4(v.position, 1);
    worldPosition = p.xyz;
    worldNormal = normalize(transpose(inverse(mat3(draw.world))) * v.normal);
    tangent = vec4(mat3(draw.world) * v.tangent.xyz, v.tangent.w * sign(determinant(mat3(draw.world))));
    uv = vec2(v.uv_x, v.uv_y);
    color = v.color;
    gl_Position = scene.viewproj * p;
}
