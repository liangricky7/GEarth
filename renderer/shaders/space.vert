#version 450
layout(push_constant) uniform Starfield {
    mat4 rotationProjection;
    vec4 viewport;
} sky;
layout(location=0) out vec2 local;
layout(location=1) flat out vec3 radiance;

uint hash(uint value) {
    value^=value>>16; value*=0x7feb352du; value^=value>>15;
    value*=0x846ca68bu; return value^(value>>16);
}
float random(uint value) { return float(hash(value)&0x00ffffffu)/16777216.0; }
void main() {
    uint star=uint(gl_VertexIndex)/6u;
    uint seed=star*747796405u+20261010u;
    float y=random(seed)*2-1;
    float angle=random(seed+1u)*6.28318530718;
    float ring=sqrt(max(0,1-y*y));
    vec3 direction=vec3(ring*cos(angle),y,ring*sin(angle));
    vec4 center=sky.rotationProjection*vec4(direction,1);
    const vec2 corners[6]=vec2[](vec2(-1,-1),vec2(1,-1),vec2(1,1),vec2(-1,-1),vec2(1,1),vec2(-1,1));
    local=corners[gl_VertexIndex%6];
    float prominence=pow(random(seed+2u),8);
    float radius=mix(.9,2.4,prominence);
    vec3 tint=mix(vec3(.68,.8,1),vec3(1,.86,.68),random(seed+3u));
    radiance=mix(tint,vec3(1),.5)*mix(.18,2.5,prominence);
    if (center.w<=0) {
        gl_Position=vec4(2,2,0,1);
        return;
    }
    gl_Position=vec4(center.xy+local*radius*2/sky.viewport.xy*center.w,0,center.w);
}
