#version 450
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_buffer_reference : require
#include "vertex.glsl"
layout(push_constant) uniform DrawConstants { mat4 lightMatrix; VertexBuffer vertices; } draw;
layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
void main() {
    Vertex v = draw.vertices.vertices[gl_VertexIndex];
    gl_Position = draw.lightMatrix * vec4(v.position, 1);
    uv = vec2(v.uv_x, v.uv_y);
    color = v.color;
}
