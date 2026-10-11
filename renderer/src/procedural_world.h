#pragma once
#include "globe.h"

constexpr int FictionalCountryCount=10;
struct FictionalCountry {
    const char* id;
    const char* name;
    glm::vec3 center{}, port{};
    glm::vec3 color{};
    bool hasPort{};
};
struct ProceduralWorld {
    uint32_t width{}, height{};
    std::vector<uint32_t> pixels, normals;
    // Settlement density in red (0 on sea): high at each capital and along coasts and river valleys.
    std::vector<uint32_t> lights;
    std::vector<uint8_t> countryIds;
    std::array<FictionalCountry,FictionalCountryCount> countries;
};
float world_height(glm::vec3 direction);
int world_country(const ProceduralWorld& world,glm::vec3 direction);
ProceduralWorld generate_world(uint32_t width,uint32_t height);
std::vector<DemoRoute> world_demo_routes(const ProceduralWorld& world);
void world_self_test();
