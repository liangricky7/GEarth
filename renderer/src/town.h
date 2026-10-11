#pragma once
#include "procedural_world.h"
#include <functional>
#include <string_view>

// Illustrative ground view of one country: a few of its agents walk a market square and stop in pairs to talk.
// Who appears and what they say come from the recorded agents; positions and movement are invented.
enum TownColor { Grass, Stone, Wood, Cloth, Pants, Skin, Shirt=Skin+3, Roof=Shirt+6, TownColorCount=Roof+FictionalCountryCount };
struct Townsperson {
    std::string id, role, line;
    int shirt{}, skin{}, partner{-1};
    glm::vec2 position{}, target{};
    float heading{}, stride{}, talk{}; // stride: metres walked (drives the walk cycle); talk: seconds left in a chat
};
struct Town {
    std::vector<Townsperson> people;
    bool recorded{};
    float clock{};
    uint32_t seed{1};
    void update(float seconds);
    // One call per unit cube (centred at the origin) to draw: colour index and its transform.
    void draw(int country, const std::function<void(int,const glm::mat4&)>& box) const;
    float random();
    void wander(Townsperson&);
};
// Country info panel: totals over countryId's agents in a renderer snapshot. Health and wellbeing are 0-100 averages.
struct CountryAgents {
    int people{}, hungry{};
    float health{}, wellbeing{};
    int64_t cashMinor{};
    std::vector<std::pair<std::string,int>> roles; // most common first
};
CountryAgents country_agents(std::string_view snapshot, std::string_view countryId);
// Agents of countryId from a renderer snapshot; previous (may be empty) turns inventory changes into trade lines.
Town town_from_snapshot(std::string_view snapshot, std::string_view previous, std::string_view countryId, size_t limit);
Town demo_town(size_t count);
// Unit cubes, one per TownColor, 36 indices each starting at colour*36.
void town_geometry(const ProceduralWorld&, std::vector<Vertex>&, std::vector<uint32_t>&);
void town_self_test();
