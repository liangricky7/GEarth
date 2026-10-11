#pragma once
#include "procedural_world.h"
#include <filesystem>
#include <string_view>
#include <utility>

// Read-only event replay. Geometry comes from the viewer, never from ledger payments.
struct EventReplay {
    struct Event {
        int tick{}, sequence{}, from{-1}, to{-1}, expires{-1};
        bool open{}, delivered{};
        std::string id, type, detail, policy, cargo, good;
        int travelTicks{}, quantity{};
        std::vector<int> path;
        bool shipment() const { return type=="shipment_dispatched" || type=="shipment_rerouted"; }
    };
    struct Route { int from{}, to{}, travelTicks{}; };
    std::string run;
    std::vector<Event> events;
    std::vector<Route> routes;
    std::vector<int> mappedEvents;
    float time{}, speed{.5f};
    int lastTick{}, selected{-1};
    bool playing{true};
    bool showAll{};
    static EventReplay parse(std::string_view json, const ProceduralWorld& world, bool allowEmpty = false);
    static EventReplay load(const std::filesystem::path&, const ProceduralWorld&);
    bool active(size_t index) const;
    std::pair<int,float> shipment_progress(const Event&) const;
    void advance(float seconds);
    void draw(const ProceduralWorld&, const std::vector<DemoRoute>& vehicleRoutes, const glm::mat4& viewproj, glm::vec3 eye,
              bool panels, float& yaw, float& pitch, bool showShipments = true);
};
void event_replay_self_test();
