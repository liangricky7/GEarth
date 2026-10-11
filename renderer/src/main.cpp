#include "vk_engine.h"
#include <cstdio>
#include <charconv>
#include <iostream>

int main(int argc, char** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::optional<std::filesystem::path> mesh, capture, replay, planeModel, shipModel, events, stream;
    bool checkStream=false;
    int checkTick=0;
    int frames = 0;
    bool smoke = false, environment = false;
    std::optional<VulkanEngine::OceanView> ocean;
    try {
        for (int i = 1; i < argc; ++i) {
            const std::string_view arg = argv[i];
            if (arg == "--self-test") { VulkanEngine::self_test(); return 0; }
            if (arg == "--smoke") smoke = true;
            else if (arg == "--mesh" && i + 1 < argc) mesh = argv[++i];
            else if (arg == "--environment" && i + 1 < argc) { mesh = argv[++i]; environment = true; }
            else if (arg == "--capture" && i + 1 < argc) capture = argv[++i];
            else if (arg == "--replay" && i + 1 < argc) replay = argv[++i];
            else if (arg == "--events" && i + 1 < argc) events = argv[++i];
            else if (arg == "--stream" && i + 1 < argc) stream = argv[++i];
            else if (arg == "--check-stream" && i + 1 < argc) { stream=argv[++i]; checkStream=true; }
            else if (arg == "--tick" && i + 1 < argc) {
                std::string_view number=argv[++i]; auto parsed=std::from_chars(number.data(),number.data()+number.size(),checkTick);
                if (parsed.ec!=std::errc{} || parsed.ptr!=number.data()+number.size() || checkTick<0) throw std::runtime_error("Invalid --tick");
            }
            else if (arg == "--plane-model" && i + 1 < argc) planeModel = argv[++i];
            else if (arg == "--ship-model" && i + 1 < argc) shipModel = argv[++i];
            else if (arg == "--scene" && i + 1 < argc) {
                std::string_view scene = argv[++i];
                if (scene == "plane") ocean = VulkanEngine::OceanView::Plane;
                else if (scene == "ship") ocean = VulkanEngine::OceanView::Ship;
                else if (scene == "globe") ocean.reset();
                else throw std::runtime_error("--scene must be globe, plane or ship");
            }
            else if (arg == "--frames" && i + 1 < argc) {
                std::string_view number = argv[++i];
                auto parsed = std::from_chars(number.data(), number.data() + number.size(), frames);
                if (parsed.ec != std::errc{} || parsed.ptr != number.data() + number.size() || frames < 1)
                    throw std::runtime_error("--frames requires a positive integer");
            } else if (arg != "--smoke") {
                throw std::runtime_error("Usage: renderer [--stream DIRECTORY | --replay run.replay | --events run.events.json | --mesh model.glb | --environment scene.gltf] [--smoke] [--frames N] [--capture image.ppm] [--scene globe|plane|ship] [--plane-model cockpit.gltf] [--ship-model ship.gltf] [--self-test | --check-stream DIRECTORY --tick DAY]");
            }
        }
        if (stream && (mesh || replay || events || ocean || planeModel || shipModel)) throw std::runtime_error("--stream requires the globe without other replay/model options");
        if (checkStream) {
            auto data=PipelineReplay::load(*stream,generate_world(64,32));
            auto poses=data.cargo_at(float(checkTick));
            fmt::print("{{\"frames\":{},\"events\":{},\"state_tick\":{},\"last_tick\":{},\"playback_seconds\":{},\"cargo\":[",data.frames.size(),data.events.events.size(),data.frames[data.frame_index(float(checkTick))].tick,data.frames.back().tick,
                float(data.frames.back().tick-data.frames.front().tick)/data.speed);
            for (size_t i=0;i<poses.size();++i) fmt::print("{}{{\"route\":{},\"progress\":{}}}",i ? "," : "",poses[i].route,poses[i].progress);
            fmt::print("]}}\n"); return 0;
        }
        if (mesh && !std::filesystem::is_regular_file(*mesh)) throw std::runtime_error("Mesh file does not exist");
        if (replay && !std::filesystem::is_regular_file(*replay)) throw std::runtime_error("Replay file does not exist");
        for (const auto& vehicle : {planeModel, shipModel})
            if (vehicle && !std::filesystem::is_regular_file(*vehicle)) throw std::runtime_error("Vehicle model file does not exist");
        if (replay && mesh) throw std::runtime_error("--replay drives the globe; it cannot be combined with --mesh");
        if (smoke && frames == 0) frames = 12;
        if (events && (mesh || replay || ocean || planeModel || shipModel)) throw std::runtime_error("--events requires the globe without other replay or model options");
        if (events && !std::filesystem::is_regular_file(*events)) throw std::runtime_error("Event JSON does not exist");
        std::future<PipelineReplay> recording;
        // Immutable recording parsing and graphics startup can overlap; both use the same fixed capitals.
        if (stream && std::filesystem::is_regular_file(*stream)) recording=std::async(std::launch::async,
            [path=*stream] { return PipelineReplay::load(path,generate_world(64,32)); });
        VulkanEngine engine;
        if (ocean && mesh) throw std::runtime_error("Plane and ship views cannot be combined with --mesh");
        engine.init(mesh, smoke, replay, ocean, planeModel, shipModel, events, environment);
        if (stream) engine.load_stream(*stream,recording.valid() ? std::optional(recording.get()) : std::nullopt);
        engine.run(frames, capture);
        engine.cleanup();
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
