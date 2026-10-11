#include "globe.h"
#include <glm/gtc/constants.hpp>
#include <algorithm>
#include <cmath>

glm::vec3 globe_position(float latitude, float longitude) {
    float lat=glm::radians(latitude), angle=glm::radians(longitude);
    return {std::cos(lat)*std::cos(angle),std::sin(lat),-std::cos(lat)*std::sin(angle)};
}
glm::vec3 globe_interpolate(glm::vec3 a, glm::vec3 b, float fraction) {
    a=glm::normalize(a); b=glm::normalize(b);
    float t=std::clamp(fraction,0.f,1.f), dot=std::clamp(glm::dot(a,b),-1.f,1.f);
    if (t==0) return a;
    if (t==1) return b;
    if (dot>.9999f) return glm::normalize(glm::mix(a,b,t));
    if (dot<-.9999f) {
        glm::vec3 axis=std::abs(a.y)<.9f ? glm::vec3(0,1,0) : glm::vec3(1,0,0);
        glm::vec3 tangent=glm::normalize(glm::cross(axis,a));
        return a*std::cos(glm::pi<float>()*t)+tangent*std::sin(glm::pi<float>()*t);
    }
    float angle=std::acos(dot);
    return glm::normalize((std::sin((1-t)*angle)*a+std::sin(t*angle)*b)/std::sin(angle));
}
glm::vec3 route_direction(std::span<const glm::vec3> points, float fraction) {
    if (points.empty()) throw std::runtime_error("Route has no waypoints");
    if (points.size()==1) return glm::normalize(points.front());
    float total=0;
    for (size_t i=1;i<points.size();++i) total+=std::acos(std::clamp(glm::dot(points[i-1],points[i]),-1.f,1.f));
    float remaining=std::clamp(fraction,0.f,1.f)*total;
    for (size_t i=1;i<points.size();++i) {
        float length=std::acos(std::clamp(glm::dot(points[i-1],points[i]),-1.f,1.f));
        if (length>.00001f && remaining<=length) return globe_interpolate(points[i-1],points[i],remaining/length);
        remaining-=length;
    }
    return points.back();
}
glm::vec3 route_position(const DemoRoute& route, float fraction) {
    float t=std::clamp(fraction,0.f,1.f);
    float radius=1.015f+(route.plane ? .05f*std::sin(glm::pi<float>()*t) : 0.f);
    return route_direction(route.points,t)*radius;
}
void vehicle_geometry(bool plane, std::vector<Vertex>& vertices, std::vector<uint32_t>& indices) {
    auto triangle=[&](glm::vec3 a,glm::vec3 b,glm::vec3 c,glm::vec4 color=glm::vec4(1)) {
        glm::vec3 normal=glm::normalize(glm::cross(b-a,c-a));
        for (auto p : {a,b,c}) { Vertex v; v.position=p; v.normal=normal; v.color=color; indices.push_back(uint32_t(vertices.size())); vertices.push_back(v); }
    };
    auto box=[&](glm::vec3 minimum,glm::vec3 maximum,glm::vec4 color) {
        glm::vec3 corners[8];
        for (int i=0;i<8;++i) corners[i]={i&1 ? maximum.x : minimum.x,i&2 ? maximum.y : minimum.y,i&4 ? maximum.z : minimum.z};
        const int faces[6][4]={{0,4,6,2},{1,3,7,5},{0,1,5,4},{2,6,7,3},{0,2,3,1},{4,5,7,6}};
        for (auto& face : faces) { triangle(corners[face[0]],corners[face[1]],corners[face[2]],color); triangle(corners[face[0]],corners[face[2]],corners[face[3]],color); }
    };
    if (plane) {
        const glm::vec3 outline[]={{0,.005f,.055f},{.006f,.005f,.019f},{.045f,.005f,-.01f},{.045f,.005f,-.02f},
            {.006f,.005f,-.009f},{.006f,.005f,-.04f},{.019f,.005f,-.049f},{.019f,.005f,-.055f},
            {0,.005f,-.048f},{-.019f,.005f,-.055f},{-.019f,.005f,-.049f},{-.006f,.005f,-.04f},
            {-.006f,.005f,-.009f},{-.045f,.005f,-.02f},{-.045f,.005f,-.01f},{-.006f,.005f,.019f}};
        for (int i=0;i<16;++i) triangle({0,.005f,0},outline[i],outline[(i+1)%16]);
        box({-.003f,0,-.035f},{.003f,.012f,.032f},{1,1,1,1});
        triangle({0,.01f,-.045f},{0,.029f,-.041f},{0,.012f,-.025f});
    } else {
        const glm::vec3 deck[]={{-.013f,.004f,-.038f},{.013f,.004f,-.038f},{.013f,.004f,.026f},{0,.004f,.045f},{-.013f,.004f,.026f}};
        for (int i=0;i<5;++i) {
            triangle({0,.004f,0},deck[i],deck[(i+1)%5]);
            triangle(deck[i],{deck[i].x,-.006f,deck[i].z},deck[(i+1)%5]);
        }
        box({-.008f,.004f,-.027f},{.008f,.02f,-.008f},{1,1,1,1});
        box({-.009f,.004f,0},{.009f,.012f,.02f},{.25f,.6f,1,1});
    }
}
void route_ribbon(const DemoRoute& route, float width, std::vector<Vertex>& vertices, std::vector<uint32_t>& indices) {
    constexpr int Segments=64;
    for (int i=0;i<=Segments;++i) {
        float t=float(i)/Segments;
        glm::vec3 p=route_position(route,t), up=glm::normalize(p);
        glm::vec3 along=route_position(route,std::min(1.f,t+.001f))-route_position(route,std::max(0.f,t-.001f));
        glm::vec3 side=glm::normalize(glm::cross(up,along))*width*.5f;
        for (int edge=0;edge<2;++edge) {
            Vertex v; v.position=p+(edge ? side : -side); v.normal=up; v.uv_x=t; v.uv_y=float(edge);
            vertices.push_back(v);
        }
        if (i<Segments) {
            uint32_t a=uint32_t(vertices.size())-2;
            indices.insert(indices.end(),{a,a+1,a+2,a+1,a+3,a+2});
        }
    }
}
void globe_self_test() {
    auto check=[](bool valid) { if (!valid) throw std::runtime_error("Globe route self-check failed"); };
    check(glm::length(globe_position(90,0)-glm::vec3(0,1,0))<.0001f);
    check(glm::length(globe_position(0,-180)-glm::vec3(-1,0,0))<.0001f);
    check(glm::length(globe_position(0,0)-glm::vec3(1,0,0))<.0001f);
    check(glm::length(globe_position(0,-90)-glm::vec3(0,0,1))<.0001f);
    check(std::abs(glm::length(globe_interpolate({1,0,0},{-1,0,0},.5f))-1)<.0001f);
    std::vector<glm::vec3> identical{{1,0,0},{1,0,0}};
    check(glm::length(route_direction(identical,.5f)-identical.front())<.0001f);
    DemoRoute route{"test",true,30,0,{{1,0,0},{0,0,1}}};
    check(glm::length(route_direction(route.points,0)-route.points.front())<.0001f);
    check(glm::length(route_direction(route.points,1)-route.points.back())<.0001f);
    for (int i=0;i<=20;++i) check(glm::length(route_position(route,i/20.f))>1.f);
    check(glm::length(route_position(route,.5f))>1.06f);
    for (bool plane : {false,true}) {
        std::vector<Vertex> vertices; std::vector<uint32_t> indices;
        vehicle_geometry(plane,vertices,indices);
        check(!indices.empty() && indices.size()%3==0);
        for (const auto& v : vertices) check(std::abs(glm::length(v.normal)-1)<.0001f);
    }
    std::vector<Vertex> ribbon; std::vector<uint32_t> ribbonIndices;
    route_ribbon(route,.004f,ribbon,ribbonIndices);
    check(ribbon.size()==130 && ribbonIndices.size()==64*6);
    for (auto index : ribbonIndices) check(index<ribbon.size());
    for (const auto& v : ribbon) check(glm::length(v.position)>1.f && v.uv_x>=0 && v.uv_x<=1);
    check(std::abs(glm::distance(ribbon[0].position,ribbon[1].position)-.004f)<.0001f);
    fmt::print("Globe coordinates, routes, ribbons and vehicle geometry passed\n");
}
