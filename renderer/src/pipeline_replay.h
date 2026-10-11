#pragma once
#include "event_replay.h"
#include "replay.h"
#include <map>

struct PipelineReplay {
    struct Cargo {
        std::string id, good;
        int quantity{}, started{}, remaining{}, edge{};
        std::vector<int> path;
    };
    struct Frame {
        int tick{}, sequence{};
        int64_t revision{};
        bool partial{};
        std::array<Replay::Nation,FictionalCountryCount> nations{};
        std::map<std::string,Cargo> cargo;
        std::vector<uint8_t> routeOpen;
        std::string snapshot;
    };
    struct Pose { std::string id, good; int route{}; float progress{}; };
    std::string run, mode, status, connection;
    std::array<std::string,FictionalCountryCount> names;
    bool historyGap{}, currentOnly{}, playing{true}, followLatest{}, liveInput{}, loop{};
    float time{}, speed{.25f};
    std::vector<Frame> frames;
    EventReplay events;
    static PipelineReplay load(const std::filesystem::path&, const ProceduralWorld&);
    size_t frame_index(float tick) const;
    std::vector<Pose> cargo_at(float tick) const;
    std::vector<uint8_t> routes_at(float tick) const;
    void advance(float seconds);
    void restart();
    void draw(const ProceduralWorld&);
private:
    int inspectedFrame{-1}, inspectedTick{-1}, category{}, selected{-1};
    std::vector<std::pair<std::string,std::string>> records;
    char filter[128]{};
};
void pipeline_replay_self_test();
