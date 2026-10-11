#version 450
layout(location=0) in vec2 local;
layout(location=1) flat in vec3 radiance;
layout(location=0) out vec4 outColor;
void main() {
    float radius=length(local);
    if (radius>=1) discard;
    float intensity=exp(-4*radius*radius)*(1-smoothstep(.7,1,radius));
    outColor=vec4(radiance,intensity);
}
