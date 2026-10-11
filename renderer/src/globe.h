#pragma once
#include "vk_types.h"

struct DemoRoute {
    std::string name;
    bool plane;
    float seconds, offset;
    std::vector<glm::vec3> points;
};
glm::vec3 globe_position(float latitude, float longitude);
glm::vec3 globe_interpolate(glm::vec3 a, glm::vec3 b, float fraction);
glm::vec3 route_direction(std::span<const glm::vec3> points, float fraction);
glm::vec3 route_position(const DemoRoute& route, float fraction);
void vehicle_geometry(bool plane, std::vector<Vertex>& vertices, std::vector<uint32_t>& indices);
// A flat ribbon along the route's vehicle path, facing away from the globe: uv.x runs along it, uv.y across.
void route_ribbon(const DemoRoute& route, float width, std::vector<Vertex>& vertices, std::vector<uint32_t>& indices);
void globe_self_test();
