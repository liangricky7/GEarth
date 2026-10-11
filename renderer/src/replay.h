#pragma once
#include "procedural_world.h"
#include <istream>

// A Faultline run converted by tools/history_to_replay.py: daily nation stats, shipments in transit and captions.
struct Replay {
    struct Route { std::string id; int from{}, to{}, travelTicks{1}; };
    struct Nation { int residents{}, unemployed{}, wars{}; float approval{}, gini{}; };
    struct Shipment { std::string good; int route{}, quantity{}, remaining{}; };
    struct Event { int country{}; std::string text; };
    struct Day {
        int tick{};
        std::array<Nation,FictionalCountryCount> nations{};
        std::vector<Shipment> shipments;
        std::vector<Event> events;
        std::vector<uint8_t> routeOpen; // per route at the end of the day; routes start open
    };
    std::string runId, mode, status;
    int seed{};
    std::array<std::string,FictionalCountryCount> governments;
    std::vector<Route> routes;
    std::vector<Day> days;
    bool is_snapshot() const { return days.size() == 1; }
};
// Countries must match the renderer's capitals; anything else is rejected rather than drawn in the wrong place.
Replay load_replay(std::istream& input, const ProceduralWorld& world);
// Fraction of a route edge covered `fraction` of the way through a day that ends with `remaining` ticks left.
float replay_progress(int travelTicks, int remaining, float fraction);
void replay_self_test();
