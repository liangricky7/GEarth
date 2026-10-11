#include "vk_engine.h"
#include "vk_pipelines.h"
#include "vk_initializers.h"
#include "vk_images.h"
#include "ocean_waves.h"
#include "ao_bake.h"
#include "dds.h"
#include <VkBootstrap.h>
#include <SDL_vulkan.h>
#include <imgui.h>
#include <imgui_impl_sdl2.h>
#include <imgui_impl_vulkan.h>
#include <glm/gtc/matrix_transform.hpp>
#include <glm/gtc/constants.hpp>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <fstream>
#include <limits>
#include <stb_image.h>

namespace {
constexpr uint32_t ShadowResolution = 2048;
bool surface_visible(const glm::mat4& matrix, const Bounds& bounds) {
    unsigned outside = 63;
    for (int i=0;i<8;++i) {
        glm::vec3 sign{i&1 ? 1.f:-1.f,i&2 ? 1.f:-1.f,i&4 ? 1.f:-1.f};
        glm::vec4 p=matrix*glm::vec4(bounds.origin+sign*bounds.extents,1);
        unsigned planes=(p.x < -p.w ? 1:0)|(p.x > p.w ? 2:0)|(p.y < -p.w ? 4:0)|
            (p.y > p.w ? 8:0)|(p.z < 0 ? 16:0)|(p.z > p.w ? 32:0);
        outside &= planes;
    }
    return outside==0;
}
glm::vec3 view_forward(glm::vec2 angles) {
    return {std::cos(angles.y)*std::sin(angles.x),std::sin(angles.y),-std::cos(angles.y)*std::cos(angles.x)};
}
DemoRoute cargo_route(const ProceduralWorld& world,int from,int to,int ticks,const std::string& name) {
    glm::vec3 a=world.countries[from].center, b=world.countries[to].center;
    // shortcut: the data has no transport mode; use a ship on mostly sea routes, otherwise a plane.
    int water=0;
    for (int i=0;i<=32;++i) water+=world_height(globe_interpolate(a,b,i/32.f))<=.035f;
    return {name,water<20,float(ticks),0,{a,b}};
}
void turn_head(glm::vec2& angles, glm::vec2 mouseDelta) {
    angles.x = std::remainder(angles.x + mouseDelta.x * .003f, glm::two_pi<float>());
    angles.y = std::clamp(angles.y - mouseDelta.y * .003f, -1.45f, 1.45f);
}
glm::mat4 head_view(glm::vec3 forward, glm::vec3 up, glm::vec2 angles) {
    // Rotate the eye in the vehicle frame; model placement and shadows retain the vehicle's heading.
    return glm::rotate(glm::mat4(1), -angles.y, glm::vec3(1,0,0)) *
        glm::rotate(glm::mat4(1), angles.x, glm::vec3(0,1,0)) * glm::lookAt(glm::vec3(0), forward, up);
}
void set_viewport(VkCommandBuffer cmd, VkExtent2D size) {
    VkViewport viewport{0, 0, float(size.width), float(size.height), 0, 1};
    VkRect2D scissor{{0, 0}, size};
    vkCmdSetViewport(cmd, 0, 1, &viewport);
    vkCmdSetScissor(cmd, 0, 1, &scissor);
}
void sphere_geometry(std::vector<Vertex>& vertices, std::vector<uint32_t>& indices) {
    constexpr uint32_t rows = 64, columns = 128;
    for (uint32_t y = 0; y <= rows; ++y) {
        float v = float(y) / rows, phi = v * glm::pi<float>();
        for (uint32_t x = 0; x <= columns; ++x) {
            float u = float(x) / columns, theta = u * glm::two_pi<float>();
            Vertex vertex;
            vertex.position = {std::sin(phi) * std::cos(theta), std::cos(phi), std::sin(phi) * std::sin(theta)};
            vertex.normal = vertex.position; vertex.uv_x = .5f-u; vertex.uv_y = v;
            vertex.tangent = { std::sin(theta), 0, -std::cos(theta), -1 };
            vertices.push_back(vertex);
        }
    }
    for (uint32_t y = 0; y < rows; ++y) for (uint32_t x = 0; x < columns; ++x) {
        uint32_t a = y * (columns + 1) + x, b = a + columns + 1;
        // Skip the degenerate triangles at the poles.
        if (y != 0) indices.insert(indices.end(), {a, a + 1, b});
        if (y != rows - 1) indices.insert(indices.end(), {a + 1, b + 1, b});
    }
}
void check(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
// Height and choppy offset of the rendered sea at rest point xz (matches ocean_displacement in ocean.glsl).
glm::vec3 sea_displacement(glm::vec2 xz, float time) {
    glm::vec3 p{0};
    for (int i = 0; i < OceanDisplaced; ++i) {
        const auto& w = oceanWaveTable[i];
        float f = w.k*(w.dx*xz.x+w.dz*xz.y) - std::sqrt(9.81f*w.k)*time + w.phase;
        p += glm::vec3(-OceanChop*w.dx*w.amplitude*std::sin(f), w.amplitude*std::cos(f), -OceanChop*w.dz*w.amplitude*std::sin(f));
    }
    // Matches ocean_group in ocean.glsl so the hull follows grouped swell as well as individual waves.
    float u = glm::dot(glm::vec2(.011f, .007f), xz) - time * .20f;
    float v = glm::dot(glm::vec2(-.006f, .013f), xz) - time * .15f;
    return p * (1.2f + .48f * std::sin(u) * std::sin(v));
}
// Meadow ground height at xz with full detail (mirrors meadow_terrain in meadow.glsl, footprint 0).
float meadow_height(glm::vec2 xz) {
    auto hash = [](int x, int y) {
        uint32_t h = uint32_t(x) * 0x9e3779b9u ^ uint32_t(y) * 0x85ebca6bu;
        h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
        return float(h >> 8) * (1.f / 16777216.f);
    };
    float height = 0, amp = 22, freq = 1 / 620.f;
    for (int i = 0; i < 5; ++i) {
        glm::vec2 p = xz * freq + glm::vec2(37.1f, 11.7f) * float(i), cell = glm::floor(p), f = p - cell, u = f * f * (3.f - 2.f * f);
        int x = int(cell.x), y = int(cell.y);
        float n = glm::mix(glm::mix(hash(x, y), hash(x + 1, y), u.x), glm::mix(hash(x, y + 1), hash(x + 1, y + 1), u.x), u.y);
        height += (n - .5f) * 2 * amp;
        amp *= .45f; freq *= 2.07f;
    }
    return height;
}
// Meadow grass bands drawn by grass.vert (keep in sync): cell spacing (m), outer radius (m), vertices per blade.
constexpr struct { float spacing, outer; uint32_t verts; } GrassBands[4] = {{.042f, 14, 15}, {.1f, 40, 9}, {.24f, 90, 3}, {.35f, 35, 75}};
}

void VulkanEngine::self_test() {
    dds_self_test();
    Bounds box{}; box.origin={0,0,.5f}; box.extents={.1f,.1f,.1f};
    check(surface_visible(glm::mat4(1),box),"Visible box culled");
    box.origin.x=2; check(!surface_visible(glm::mat4(1),box),"Offscreen box retained");
    box.origin={0,0,-.05f}; check(surface_visible(glm::mat4(1),box),"Near-plane intersection culled");
    box.origin.z=-1; check(!surface_visible(glm::mat4(1),box),"Box behind camera retained");
    auto gaze = [](glm::vec2 angles, glm::vec3 up = glm::vec3(0,1,0)) {
        return glm::vec3(glm::inverse(head_view({0,0,1}, up, angles)) * glm::vec4(0,0,-1,0));
    };
    check(glm::length(gaze({}) - glm::vec3(0,0,1)) < .0001f, "Recenter must face the vehicle heading");
    glm::vec2 head{};
    turn_head(head, {100, -100});
    auto rightUp = gaze(head);
    check(rightUp.x < 0 && rightUp.y > 0, "Mouse right/up must turn the head right/up");
    check(glm::length(gaze({glm::half_pi<float>(),0}) - glm::vec3(-1,0,0)) < .0001f,
        "Head must turn sideways without rotating the vehicle");
    check(glm::length(gaze({glm::pi<float>(),0}) - glm::vec3(0,0,-1)) < .0001f, "Head can look behind");
    check(glm::length(gaze({0,.4f}, {1,0,0}) - glm::vec3(std::sin(.4f),0,std::cos(.4f))) < .0001f,
        "Head pitch must follow vehicle roll");
    turn_head(head, {100000, -100000});
    check(std::abs(head.x) <= glm::pi<float>() && head.y == 1.45f, "Head yaw wraps and pitch stays below the pole");
    turn_head(head, {0, 100000});
    check(head.y == -1.45f && std::abs(glm::length(gaze(head))-1) < .0001f, "Downward limit keeps a valid view");
    {
        VulkanEngine vehicle;
        vehicle._swapchainExtent = {1280,800};
        vehicle._oceanSeconds = 23;
        for (auto view : {OceanView::Plane, OceanView::Ship, OceanView::Meadow}) {
            vehicle._oceanView = view;
            vehicle._headAngles = {};
            vehicle.update_ocean();
            const auto straight = vehicle._sceneData;
            vehicle._headAngles = {.7f,.2f};
            vehicle.update_ocean();
            check(vehicle._sceneData.viewproj != straight.viewproj, "Head movement must reach the rendered view");
            check(vehicle._sceneData.sunViewProjection == straight.sunViewProjection,
                "Head movement must not rotate vehicle shadows");
            vehicle._headAngles = {};
            vehicle.update_ocean();
            check(vehicle._sceneData.viewproj == straight.viewproj, "Recenter must restore the original vehicle view");
        }
        // The meadow walk keeps the eye at standing height over the hills for the whole loop.
        for (float seconds = 0; seconds < 1800; seconds += 7.3f) {
            vehicle._oceanSeconds = seconds;
            vehicle.update_ocean();
            const auto& eye = vehicle._oceanPush.params;
            float clearance = eye.w - meadow_height({eye.y, eye.z});
            check(clearance > 1.2f && clearance < 2.1f, "Meadow eye must stay at standing height above the ground");
        }
        check(meadow_height({123.4f, -56.7f}) == meadow_height({123.4f, -56.7f}) &&
            meadow_height({0, 0}) != meadow_height({300, 0}), "Meadow terrain must be deterministic and vary");
        vehicle._replay.emplace();
        vehicle._replay->days.push_back({});
        vehicle._replay->days[0].tick = 365;
        vehicle._demoSeconds = 2.75f;
        check(vehicle.replay_time() == 0.f, "A single snapshot must hold the recorded shipment positions");
        vehicle._replay->days.push_back({});
        check(vehicle.replay_time() == .75f, "Multi-day replay must retain interpolated playback");
    }
    fmt::print("Vehicle head-look self-check passed\n");
    {
        VulkanEngine cargo;
        cargo._planeIndices=3; cargo._shipIndices=6;
        cargo._replayRoutes={{"air",true,4,0,{{1,0,0},{0,0,1}}},{"sea",false,4,0,{{0,0,1},{-1,0,0}}}};
        cargo._routes=cargo._replayRoutes;
        cargo._eventReplay.emplace();
        cargo._eventReplay->routes={{0,1,4},{1,2,4}};
        for (int route=0;route<2;++route) {
            EventReplay::Event shipment;
            shipment.type="shipment_dispatched"; shipment.good="food";
            shipment.tick=1; shipment.expires=5; shipment.travelTicks=4; shipment.path={route};
            cargo._eventReplay->events.push_back(shipment);
            cargo._eventReplay->mappedEvents.push_back(route);
        }
        auto poses=[&](float tick) {
            if (cargo._pipeline) cargo._pipeline->time=tick;
            else cargo._eventReplay->time=tick;
            cargo._drawContext.OpaqueSurfaces.clear(); cargo._drawContext.TransparentSurfaces.clear();
            cargo.update_globe();
            std::vector<glm::mat4> result;
            for (const auto& object : cargo._drawContext.OpaqueSurfaces)
                if (object.indexCount==3 || object.indexCount==6) result.push_back(object.transform);
            return result;
        };
        check(poses(0).empty(),"Events must not show demo vehicles before dispatch");
        auto moving=poses(3);
        check(moving.size()==2,"Both recorded ships and planes must reach the draw list");
        check(std::abs(glm::length(glm::vec3(moving[0][3]))-1.065f)<.0001f &&
              std::abs(glm::length(glm::vec3(moving[1][3]))-1.015f)<.0001f,"Recorded ships stay low and planes use flight arcs");
        check(poses(5).empty(),"Delivered cargo must disappear from the draw list");
        cargo._demoSeconds=999;
        check(poses(3)==moving,"Backward seek restores vehicles independently of the demo clock");
        cargo._traffic=false;
        check(poses(3).empty(),"Traffic visibility must hide recorded vehicles");
        cargo._traffic=true;
        cargo._pipeline.emplace();
        auto& recorded=*cargo._pipeline;
        recorded.events.routes=cargo._eventReplay->routes;
        cargo._eventReplay.reset();
        recorded.frames.resize(2); recorded.frames.back().tick=4;
        recorded.frames.front().cargo.emplace("ship",PipelineReplay::Cargo{"ship","food",1,0,0,0,{1}});
        auto start=poses(0);
        recorded.advance(2);
        auto animated=poses(recorded.time);
        check(start.size()==1 && animated.size()==1 && start!=animated,"Recorded cargo visibly moves between integer days");
        recorded.loop=true; recorded.advance(16);
        check(poses(recorded.time)==animated,"Loop restores the same recorded vehicle pose without accumulated motion");
    }
    fmt::print("Recorded ship/plane dispatch, delivery, altitude and backward seek passed\n");
    PipelineBuilder pipeline;
    pipeline.set_color_attachment_format(VK_FORMAT_R16G16B16A16_SFLOAT);
    pipeline.enable_blending_alphablend();
    pipeline.clear();
    pipeline.set_depth_format(VK_FORMAT_D32_SFLOAT);
    pipeline.enable_depthtest(true, VK_COMPARE_OP_LESS_OR_EQUAL);
    pipeline._rasterizer.depthBiasEnable = VK_TRUE;
    pipeline.enable_blending_alphablend();
    check(pipeline._renderInfo.colorAttachmentCount == 0 && pipeline._renderInfo.pColorAttachmentFormats == nullptr,
        "Shadow pipeline must have no color target");
    pipeline.clear();
    pipeline.set_color_attachment_format(VK_FORMAT_R8G8B8A8_UNORM);
    pipeline.disable_blending(); pipeline.disable_depthtest();
    check(pipeline._renderInfo.colorAttachmentCount == 1 &&
        *pipeline._renderInfo.pColorAttachmentFormats == VK_FORMAT_R8G8B8A8_UNORM,
        "Post-process pipeline must restore one color target");
    check(pipeline._renderInfo.depthAttachmentFormat == VK_FORMAT_UNDEFINED &&
        !pipeline._rasterizer.depthBiasEnable && !pipeline._depthStencil.depthTestEnable &&
        !pipeline._depthStencil.stencilTestEnable && !pipeline._colorBlendAttachment.blendEnable &&
        pipeline._colorBlendAttachment.colorWriteMask == 0xf,
        "Reusing the builder must not retain shadow or transparent state");
    fmt::print("Pipeline reuse self-check passed\n");
    std::vector<Vertex> vertices; std::vector<uint32_t> indices;
    sphere_geometry(vertices, indices);
    check(indices.size() == 128 * 63 * 6, "Sphere triangle count");
    for (const auto& v : vertices) check(std::abs(glm::length(v.normal) - 1) < .0001f, "Sphere unit normals");
    for (size_t i = 0; i < indices.size(); i += 3) {
        for (int k = 0; k < 3; ++k) check(indices[i+k] < vertices.size(), "Sphere index bounds");
        glm::vec3 a = vertices[indices[i]].position, b = vertices[indices[i+1]].position, c = vertices[indices[i+2]].position;
        check(glm::dot(glm::cross(b-a,c-a), a+b+c) > 0, "Sphere winding / nondegenerate poles");
    }
    fmt::print("Geometry self-check passed\n");
    globe_self_test();
    world_self_test();
    replay_self_test();
    event_replay_self_test();
    pipeline_replay_self_test();
    town_self_test();
    ao_bake_self_test();
    auto projection=glm::perspective(glm::radians(45.f),1.6f,100.f,.05f);
    glm::vec4 direction(.2f,.1f,-1.f,1);
    auto skyPoint=[&](glm::vec3 eye) {
        auto matrix=projection*glm::lookAt(eye,glm::vec3(0),glm::vec3(0,1,0))*glm::translate(glm::mat4(1),eye);
        glm::vec4 clip=matrix*direction;
        return glm::vec2(clip.x,clip.y)/clip.w;
    };
    check(glm::length(skyPoint({0,0,3})-skyPoint({0,0,6}))<.00001f,"Stars must ignore camera translation");
    check(glm::length(skyPoint({0,0,3})-skyPoint({3,0,0}))>.1f,"Stars must follow camera rotation");
}

VulkanEngine::~VulkanEngine() { cleanup(); }
void VulkanEngine::load_stream(const std::filesystem::path& path, std::optional<PipelineReplay> prepared) {
    const bool folder=std::filesystem::is_directory(path);
    auto modified=std::filesystem::last_write_time(folder ? path/"index.json" : path);
    if (_pipeline && modified==_streamModified) return;
    auto next=prepared ? std::move(*prepared) : PipelineReplay::load(path,_world);
    if (_pipeline) {
        check(next.run==_pipeline->run,"A stream cannot change run identity");
        check(next.frames.back().revision>=_pipeline->frames.back().revision,"A stream cannot roll back committed frames");
        next.time=std::clamp(_pipeline->time,float(next.frames.front().tick),float(next.frames.back().tick));
        next.playing=_pipeline->playing; next.speed=_pipeline->speed; next.followLatest=_pipeline->followLatest;
        next.loop=_pipeline->loop;
    }
    std::vector<DemoRoute> routes;
    for (const auto& route : next.events.routes) routes.push_back(cargo_route(_world,route.from,route.to,route.travelTicks,""));
    _replayRoutes=std::move(routes); _pipeline=std::move(next);
    for (int i=0;i<FictionalCountryCount;++i) _world.countries[i].name=_pipeline->names[i].c_str();
    if (_routeArcs.empty()) for (size_t i=0;i<_pipeline->events.routes.size();++i) {
        const auto& route=_pipeline->events.routes[i];
        if (route.from>route.to) continue;
        RouteArc arc; arc.forward=int(i);
        for (size_t j=0;j<_pipeline->events.routes.size();++j)
            if (_pipeline->events.routes[j].from==route.to && _pipeline->events.routes[j].to==route.from) arc.backward=int(j);
        std::vector<Vertex> vertices; std::vector<uint32_t> indices;
        route_ribbon(_replayRoutes[i],.009f,vertices,indices);
        arc.indexCount=uint32_t(indices.size()); arc.mesh=uploadMesh(indices,vertices);
        _routeArcs.push_back(arc);
    }
    if (folder) _streamPath=path; else _streamPath.reset();
    _streamModified=modified;
    fmt::print("GEarth stream: {} frames through day {}, {} events\n",_pipeline->frames.size(),_pipeline->frames.back().tick,_pipeline->events.events.size());
}
void VulkanEngine::load_events(const std::filesystem::path& path) {
    check(_globe && !_replay && _scene==Scene::Globe,"Events require a globe without another replay");
    _eventReplay=EventReplay::load(path,_world);
    _replayRoutes.clear();
    for (const auto& route : _eventReplay->routes)
        _replayRoutes.push_back(cargo_route(_world,route.from,route.to,route.travelTicks,""));
    _traffic=true;
    fmt::print("Recorded event replay: {} records, ticks 0-{}\n",_eventReplay->events.size(),_eventReplay->lastTick);
}
std::string VulkanEngine::shader_path(const char* name) const {
    char* base = SDL_GetBasePath();
    if (!base) throw std::runtime_error(SDL_GetError());
    std::filesystem::path directory(base); SDL_free(base);
    return (directory / "shaders" / name).string();
}

void VulkanEngine::init(const std::optional<std::filesystem::path>& model, bool smoke, const std::optional<std::filesystem::path>& replay,
    std::optional<OceanView> ocean, const std::optional<std::filesystem::path>& planeModel, const std::optional<std::filesystem::path>& shipModel,
    const std::optional<std::filesystem::path>& events, bool environment) {
    _environment=environment;
    _smoke = smoke; _replayPath = replay;
    if (ocean) { _scene = Scene::Ocean; _oceanView = *ocean; }
    if (!model) _worldFuture = std::async(std::launch::async, [] { return generate_world(8192, 4096); });
    SDL_SetMainReady();
    // Render at the display's real pixel count: without this, Windows scaling (e.g. 150%) stretches an
    // undersized image and blurs everything. Window sizes stay in scaled points; the drawable is pixels.
    SDL_SetHint(SDL_HINT_WINDOWS_DPI_AWARENESS, "permonitorv2");
    SDL_SetHint(SDL_HINT_WINDOWS_DPI_SCALING, "1");
    check(SDL_Init(SDL_INIT_VIDEO) == 0, SDL_GetError());
    _window = SDL_CreateWindow("GEarth Renderer", SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
        1280, 800, SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE | SDL_WINDOW_ALLOW_HIGHDPI | (smoke ? SDL_WINDOW_HIDDEN : 0));
    check(_window != nullptr, SDL_GetError());
    if (_environment) SDL_SetWindowTitle(_window,"GEarth Renderer - Environment");
    init_vulkan(); init_commands(); init_sync_structures();
    create_swapchain();
    VkFormatFeatureFlags2 depthRequired = VK_FORMAT_FEATURE_2_DEPTH_STENCIL_ATTACHMENT_BIT | VK_FORMAT_FEATURE_2_SAMPLED_IMAGE_BIT |
        VK_FORMAT_FEATURE_2_SAMPLED_IMAGE_DEPTH_COMPARISON_BIT;
    depthFormat = VK_FORMAT_UNDEFINED;
    bool linearShadow = false;
    for (auto format : {VK_FORMAT_D32_SFLOAT, VK_FORMAT_D16_UNORM}) {
        VkFormatProperties3 properties{.sType = VK_STRUCTURE_TYPE_FORMAT_PROPERTIES_3};
        VkFormatProperties2 query{.sType = VK_STRUCTURE_TYPE_FORMAT_PROPERTIES_2, .pNext = &properties};
        vkGetPhysicalDeviceFormatProperties2(_chosenGPU, format, &query);
        if ((properties.optimalTilingFeatures & depthRequired) != depthRequired) continue;
        bool linear = properties.optimalTilingFeatures & VK_FORMAT_FEATURE_2_SAMPLED_IMAGE_FILTER_LINEAR_BIT;
        if (depthFormat == VK_FORMAT_UNDEFINED || linear) { depthFormat = format; linearShadow = linear; }
        if (linear) break;
    }
    check(depthFormat != VK_FORMAT_UNDEFINED, "GPU lacks sampled comparison depth");
    create_targets();
    _shadowImage = create_image({ShadowResolution, ShadowResolution, 1}, depthFormat,
        VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT);
    VkSamplerCreateInfo sampler{.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
    sampler.minFilter = sampler.magFilter = linearShadow ? VK_FILTER_LINEAR : VK_FILTER_NEAREST;
    sampler.addressModeU = sampler.addressModeV = sampler.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER;
    sampler.borderColor = VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE;
    sampler.compareEnable = VK_TRUE; sampler.compareOp = VK_COMPARE_OP_LESS_OR_EQUAL;
    VK_CHECK(vkCreateSampler(_device, &sampler, nullptr, &_shadowSampler));
    sampler.compareEnable = VK_FALSE;
    sampler.minFilter = sampler.magFilter = VK_FILTER_LINEAR;
    sampler.addressModeU = sampler.addressModeV = sampler.addressModeW = VK_SAMPLER_ADDRESS_MODE_REPEAT;
    sampler.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR;
    sampler.maxLod = VK_LOD_CLAMP_NONE;
    sampler.anisotropyEnable = _samplerAnisotropySupported;
    sampler.maxAnisotropy = material_anisotropy();
    VK_CHECK(vkCreateSampler(_device, &sampler, nullptr, &_defaultSamplerLinear));
    sampler.anisotropyEnable = VK_FALSE;
    sampler.addressModeU = sampler.addressModeV = sampler.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sampler.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    sampler.maxLod = 0;
    VK_CHECK(vkCreateSampler(_device, &sampler, nullptr, &_postSampler));
    sampler.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR;
    sampler.maxLod = VK_LOD_CLAMP_NONE;
    VK_CHECK(vkCreateSampler(_device, &sampler, nullptr, &_bloomSampler));
    uint32_t white = 0xffffffff, black = 0xff000000, grey = 0xff808080, normal = 0xffff8080;
    uint32_t checker[] = {0xffff00ff, 0xff000000, 0xff000000, 0xffff00ff};
    _whiteImage = create_image(&white, {1,1,1}, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT);
    _blackImage = create_image(&black, {1,1,1}, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT);
    _greyImage = create_image(&grey, {1,1,1}, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT);
    _flatNormalImage = create_image(&normal, {1,1,1}, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT);
    _errorCheckerboardImage = create_image(checker, {2,2,1}, VK_FORMAT_R8G8B8A8_SRGB, VK_IMAGE_USAGE_SAMPLED_BIT);
    init_descriptors(); init_pipelines(); init_scene(model);
    // Without --plane-model/--ship-model the globe build carries the bundled models (models/ next to the exe), so the
    // plane and ship views always have their cockpit and ship however the renderer was started.
    std::filesystem::path bundled;
    if (char* base = SDL_GetBasePath()) { bundled = std::filesystem::path(base) / "models"; SDL_free(base); }
    for (int i = 0; i < 2; ++i) {
        std::optional<std::filesystem::path> path = i ? shipModel : planeModel;
        auto fallback = bundled / (i ? "cargo_ship" : "boeing_737_cockpit") / "scene.gltf";
        // Smoke runs skip the bundled models so checks stay fast and do not depend on what ships beside the exe.
        if (!path && !model && !smoke && !events && std::filesystem::is_regular_file(fallback)) path = fallback;
        _vehiclePaths[i] = path;
    }
    if (_scene == Scene::Ocean && _oceanView != OceanView::Meadow) load_vehicle(_oceanView == OceanView::Ship);
    init_imgui();
    if (events) load_events(*events);
}

void VulkanEngine::create_swapchain() {
    int width, height; SDL_Vulkan_GetDrawableSize(_window, &width, &height);
    check(width > 0 && height > 0, "Cannot create a zero-size swapchain");
    auto result = vkb::SwapchainBuilder{_chosenGPU, _device, _surface}
        .set_desired_format({_swapchainImageFormat, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR})
        .set_desired_present_mode(VK_PRESENT_MODE_FIFO_KHR)
        .set_desired_min_image_count(3)
        .set_desired_extent(width, height).add_image_usage_flags(VK_IMAGE_USAGE_TRANSFER_DST_BIT).build().value();
    _swapchain = result.swapchain; _swapchainExtent = result.extent;
    check(result.image_format == _swapchainImageFormat, "Renderer requires an UNORM swapchain for explicit sRGB encoding");
    _swapchainImages = result.get_images().value(); _swapchainImageViews = result.get_image_views().value();
    _swapchainRenderSemaphores.resize(_swapchainImages.size());
    auto info = vkinit::semaphore_create_info();
    for (auto& semaphore : _swapchainRenderSemaphores) VK_CHECK(vkCreateSemaphore(_device, &info, nullptr, &semaphore));
}
void VulkanEngine::destroy_swapchain() {
    for (auto semaphore : _swapchainRenderSemaphores) vkDestroySemaphore(_device, semaphore, nullptr);
    for (auto view : _swapchainImageViews) vkDestroyImageView(_device, view, nullptr);
    if (_swapchain) vkDestroySwapchainKHR(_device, _swapchain, nullptr);
    _swapchainRenderSemaphores.clear(); _swapchainImages.clear(); _swapchainImageViews.clear(); _swapchain = {};
}
void VulkanEngine::create_targets() {
    VkExtent3D size{_swapchainExtent.width, _swapchainExtent.height, 1};
    auto colorUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    _drawImage = create_image(size, colorFormat, colorUsage);
    _depthImage = create_image(size, depthFormat, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT);
    _tonemapImage = create_image(size, VK_FORMAT_R8G8B8A8_UNORM, colorUsage);
    _finalImage = create_image(size, VK_FORMAT_R8G8B8A8_UNORM, colorUsage);
    // Half-resolution HDR copy whose mip chain is the bloom blur pyramid.
    _bloomImage = create_image({std::max(1u,size.width/2),std::max(1u,size.height/2),1}, colorFormat,
        VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT, true);
}
void VulkanEngine::destroy_targets() {
    for (auto* image : {&_drawImage, &_depthImage, &_tonemapImage, &_finalImage, &_bloomImage}) { destroy_image(*image); *image = {}; }
}
void VulkanEngine::resize_swapchain() {
    VK_CHECK(vkDeviceWaitIdle(_device));
    if (_imguiInitialized) { ImGui_ImplVulkan_Shutdown(); ImGui_ImplSDL2_Shutdown(); ImGui::DestroyContext(); _imguiInitialized = false; }
    destroy_targets(); destroy_swapchain(); create_swapchain(); create_targets(); write_post_descriptors();
    init_imgui(); _resize = false;
}

void VulkanEngine::init_descriptors() {
    std::array ratios{DescriptorAllocatorGrowable::PoolSizeRatio{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 2},
        DescriptorAllocatorGrowable::PoolSizeRatio{VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 4}};
    _descriptors.init(_device, 32, ratios);
    DescriptorLayoutBuilder builder;
    builder.add_binding(0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
    builder.add_binding(1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    _gpuSceneDataDescriptorLayout = builder.build(_device, VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT);
    for (auto& frame : _frames) {
        frame.sceneBuffer = create_buffer(sizeof(GPUSceneData), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT, VMA_MEMORY_USAGE_CPU_TO_GPU);
        frame.sceneDescriptor = _descriptors.allocate(_device, _gpuSceneDataDescriptorLayout);
        DescriptorWriter writer;
        writer.write_buffer(0, frame.sceneBuffer.buffer, sizeof(GPUSceneData), 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
        writer.write_image(1, _shadowImage.imageView, _shadowSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
        writer.update_set(_device, frame.sceneDescriptor);
    }
    builder.clear(); builder.add_binding(0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER); builder.add_binding(1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    _postLayout = builder.build(_device, VK_SHADER_STAGE_FRAGMENT_BIT);
    _tonemapSet = _descriptors.allocate(_device, _postLayout); _fxaaSet = _descriptors.allocate(_device, _postLayout);
    write_post_descriptors();
}
void VulkanEngine::write_post_descriptors() {
    DescriptorWriter writer;
    writer.write_image(0, _drawImage.imageView, _postSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.write_image(1, _bloomImage.imageView, _bloomSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.update_set(_device, _tonemapSet); writer.clear();
    writer.write_image(0, _tonemapImage.imageView, _postSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.write_image(1, _bloomImage.imageView, _bloomSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.update_set(_device, _fxaaSet);
}

void VulkanEngine::init_pipelines() {
    metalRoughMaterial.build_pipelines(this);
    VkPushConstantRange shadowPush{VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(GPUDrawPushConstants)};
    auto layout = vkinit::pipeline_layout_create_info();
    layout.setLayoutCount = 1; layout.pSetLayouts = &metalRoughMaterial.materialLayout;
    layout.pushConstantRangeCount = 1; layout.pPushConstantRanges = &shadowPush;
    VK_CHECK(vkCreatePipelineLayout(_device, &layout, nullptr, &_shadowPipeline.layout));
    ShaderModules shaders(_device);
    auto shadowVert = shaders.load(shader_path("shadow_depth.vert.spv"));
    auto shadowFrag = shaders.load(shader_path("shadow_mask.frag.spv"));
    auto fullscreen = shaders.load(shader_path("fullscreen.vert.spv"));
    PipelineBuilder builder;
    builder._pipelineLayout = _shadowPipeline.layout; builder.set_shaders(shadowVert, shadowFrag);
    builder.set_input_topology(VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST); builder.set_polygon_mode(VK_POLYGON_MODE_FILL);
    builder.set_cull_mode(VK_CULL_MODE_NONE, VK_FRONT_FACE_CLOCKWISE); builder.set_multisampling_none();
    builder.disable_blending();
    builder.enable_depthtest(true, VK_COMPARE_OP_LESS_OR_EQUAL);
    builder._rasterizer.depthBiasEnable = VK_TRUE;
    builder._rasterizer.depthBiasConstantFactor = 1.25f;
    builder._rasterizer.depthBiasSlopeFactor = 2.75f;
    builder.set_depth_format(depthFormat); _shadowPipeline.pipeline = builder.build_pipeline(_device);
    check(_shadowPipeline.pipeline != VK_NULL_HANDLE, "Shadow pipeline failed");
    for (bool fxaa : {false, true}) {
        auto& pipeline = fxaa ? _fxaaPipeline : _tonemapPipeline;
        VkPushConstantRange range{VK_SHADER_STAGE_FRAGMENT_BIT, 0, 32u};
        layout.pSetLayouts = &_postLayout; layout.pPushConstantRanges = &range;
        VK_CHECK(vkCreatePipelineLayout(_device, &layout, nullptr, &pipeline.layout));
        auto frag = shaders.load(shader_path(fxaa ? "fxaa.frag.spv" : "tonemap.frag.spv"));
        builder.clear(); builder._pipelineLayout = pipeline.layout; builder.set_shaders(fullscreen, frag);
        builder.set_input_topology(VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST); builder.set_polygon_mode(VK_POLYGON_MODE_FILL);
        builder.set_cull_mode(VK_CULL_MODE_NONE, VK_FRONT_FACE_CLOCKWISE); builder.set_multisampling_none();
        builder.disable_blending(); builder.disable_depthtest(); builder.set_color_attachment_format(VK_FORMAT_R8G8B8A8_UNORM);
        pipeline.pipeline = builder.build_pipeline(_device);
        check(pipeline.pipeline != VK_NULL_HANDLE, "Post-process pipeline failed");
    }
    VkPushConstantRange starPush{VK_SHADER_STAGE_VERTEX_BIT,0,sizeof(StarfieldPushConstants)};
    layout.setLayoutCount=0; layout.pSetLayouts=nullptr;
    layout.pushConstantRangeCount=1; layout.pPushConstantRanges=&starPush;
    VK_CHECK(vkCreatePipelineLayout(_device,&layout,nullptr,&_spacePipeline.layout));
    auto starVertex = shaders.load(shader_path("space.vert.spv"));
    auto stars = shaders.load(shader_path("space.frag.spv"));
    builder.clear(); builder._pipelineLayout=_spacePipeline.layout; builder.set_shaders(starVertex,stars);
    builder.set_input_topology(VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST); builder.set_polygon_mode(VK_POLYGON_MODE_FILL);
    builder.set_cull_mode(VK_CULL_MODE_NONE,VK_FRONT_FACE_COUNTER_CLOCKWISE); builder.set_multisampling_none();
    builder.enable_blending_additive(); builder.disable_depthtest(); builder.set_color_attachment_format(colorFormat);
    builder.set_depth_format(depthFormat);
    _spacePipeline.pipeline=builder.build_pipeline(_device);
    check(_spacePipeline.pipeline!=VK_NULL_HANDLE,"Space pipeline failed");
    // Vehicle views: a fullscreen sky, then a camera-centred water grid generated in the vertex shader.
    VkPushConstantRange oceanPush{VK_SHADER_STAGE_VERTEX_BIT|VK_SHADER_STAGE_FRAGMENT_BIT,0,sizeof(OceanPushConstants)};
    layout.setLayoutCount=1; layout.pSetLayouts=&_gpuSceneDataDescriptorLayout; layout.pPushConstantRanges=&oceanPush;
    auto skyFrag = shaders.load(shader_path("sky.frag.spv"));
    auto waterVert = shaders.load(shader_path("water.vert.spv"));
    auto waterFrag = shaders.load(shader_path("water.frag.spv"));
    // Meadow view: the same sky, a terrain grid like the water's, and grass blades generated in grass.vert.
    auto terrainVert = shaders.load(shader_path("terrain.vert.spv")), terrainFrag = shaders.load(shader_path("terrain.frag.spv"));
    auto grassVert = shaders.load(shader_path("grass.vert.spv")), grassFrag = shaders.load(shader_path("grass.frag.spv"));
    std::pair<MaterialPipeline*, std::pair<VkShaderModule, VkShaderModule>> passes[] = {{&_skyPipeline, {fullscreen, skyFrag}},
        {&_waterPipeline, {waterVert, waterFrag}}, {&_terrainPipeline, {terrainVert, terrainFrag}}, {&_grassPipeline, {grassVert, grassFrag}}};
    for (auto& [target, stages] : passes) {
        auto& pipeline = *target;
        bool depth = target != &_skyPipeline; // everything but the fullscreen sky is real geometry
        VK_CHECK(vkCreatePipelineLayout(_device,&layout,nullptr,&pipeline.layout));
        builder.clear(); builder._pipelineLayout=pipeline.layout;
        builder.set_shaders(stages.first,stages.second);
        builder.set_input_topology(VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST); builder.set_polygon_mode(VK_POLYGON_MODE_FILL);
        builder.set_cull_mode(VK_CULL_MODE_NONE,VK_FRONT_FACE_COUNTER_CLOCKWISE); builder.set_multisampling_none();
        builder.disable_blending();
        if (depth) builder.enable_depthtest(true,VK_COMPARE_OP_GREATER_OR_EQUAL); else builder.disable_depthtest();
        builder.set_color_attachment_format(colorFormat); builder.set_depth_format(depthFormat);
        pipeline.pipeline=builder.build_pipeline(_device);
        check(pipeline.pipeline!=VK_NULL_HANDLE,"Ocean pipeline failed");
    }
}

void VulkanEngine::init_scene(const std::optional<std::filesystem::path>& model) {
    std::vector<Vertex> vertices; std::vector<uint32_t> indices;
    sphere_geometry(vertices, indices); _sphereIndices = uint32_t(indices.size()); _sphere = uploadMesh(indices, vertices);
    vertices.clear(); indices = {0,2,1,0,3,2};
    for (glm::vec3 p : {glm::vec3{-3,-1.3f,-3}, {3,-1.3f,-3}, {3,-1.3f,3}, {-3,-1.3f,3}}) {
        Vertex v; v.position = p; v.normal = {0,1,0}; vertices.push_back(v);
    }
    _groundIndices = uint32_t(indices.size()); _ground = uploadMesh(indices, vertices);
    _defaultMaterialBuffer = create_buffer(3 * sizeof(GLTFMetallic_Roughness::MaterialConstants), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT, VMA_MEMORY_USAGE_CPU_TO_GPU);
    auto* data = static_cast<GLTFMetallic_Roughness::MaterialConstants*>(_defaultMaterialBuffer.info.pMappedData);
    data[0] = {}; data[1] = {}; data[2] = {};
    data[0].colorFactors = {.06f,.32f,.6f,1}; data[0].metal_rough_factors = {0,.3f,0,0};
    data[1].colorFactors = {.35f,.38f,.42f,1}; data[1].metal_rough_factors = {0,.85f,0,0};
    data[2].colorFactors = {1,1,1,1}; data[2].metal_rough_factors = {0,.8f,0,0};
    for (int i = 0; i < 3; ++i) {
        GLTFMetallic_Roughness::MaterialResources r;
        r.colorImage = r.metalRoughImage = _whiteImage; r.normalImage = _flatNormalImage;
        r.colorSampler = r.metalRoughSampler = r.normalSampler = _defaultSamplerLinear;
        r.dataBuffer = _defaultMaterialBuffer.buffer; r.dataBufferOffset = i * sizeof(*data);
        (i == 0 ? _sphereMaterial : i == 1 ? _groundMaterial : _townMaterial) = metalRoughMaterial.write_material(_device, MaterialPass::MainColor, r, _descriptors);
    }
    VK_CHECK(vmaFlushAllocation(_allocator,_defaultMaterialBuffer.allocation,0,VK_WHOLE_SIZE));
    _globe=!model.has_value();
    if (_globe) init_globe();
    if (model) {
        auto loaded = loadGltf(this, *model); check(loaded.has_value(), "Mesh import failed"); _model = *loaded;
        DrawContext context; _model->Draw(glm::mat4(1), context);
        glm::vec3 minimum(std::numeric_limits<float>::max()), maximum(std::numeric_limits<float>::lowest());
        auto fit = [&](const auto& surfaces) {
            for (const auto& object : surfaces) for (int corner = 0; corner < 8; ++corner) {
                glm::vec3 sign{corner & 1 ? 1.f : -1.f, corner & 2 ? 1.f : -1.f, corner & 4 ? 1.f : -1.f};
                glm::vec3 p(object.transform * glm::vec4(object.bounds.origin + sign * object.bounds.extents,1));
                minimum = glm::min(minimum,p); maximum = glm::max(maximum,p);
            }
        };
        fit(context.OpaqueSurfaces); fit(context.TransparentSurfaces);
        glm::vec3 extent = maximum - minimum;
        float longest = std::max({extent.x,extent.y,extent.z});
        check(std::isfinite(longest) && longest > .000001f, "Model has empty or invalid bounds");
        _modelTransform = glm::scale(glm::mat4(1),glm::vec3(2.f/longest)) * glm::translate(glm::mat4(1),-(minimum+maximum)*.5f);
        if (_environment) {
            _modelTransform=glm::mat4(1); _showGround=false; _ambient=.35f; _sunIntensity=4; _exposure=.8f;
            _cameraPosition=(minimum+maximum)*.5f+glm::vec3(0,0,longest*.6f);
            if (_model->camera) {
                _cameraPosition=glm::vec3((*_model->camera)[3]);
                glm::vec3 forward=glm::normalize(glm::vec3(*_model->camera*glm::vec4(0,0,-1,0)));
                _cameraAngles={std::atan2(forward.x,-forward.z),std::asin(std::clamp(forward.y,-1.f,1.f))};
            }
            _cameraStart=_cameraPosition; _cameraStartAngles=_cameraAngles;
            if (_model->sunDirection) _sunDirection=*_model->sunDirection;
            std::copy(_model->lights.begin(),_model->lights.end(),_sceneData.lights.begin());
            fmt::print("Environment at original scale; camera {:.2f}, {:.2f}, {:.2f}\n",_cameraPosition.x,_cameraPosition.y,_cameraPosition.z);
        } else fmt::print("Imported model centered and fitted to a two-unit box\n");
    }
}

void VulkanEngine::init_globe() {
    _showGround=false; _shadows=false; _yaw=.14f; _pitch=.27f; _distance=3.15f;
    _sunDirection={1.f,.35f,.55f}; _sunIntensity=3.f; _ambient=.18f; _atmosphere=true;
    char* base=SDL_GetBasePath(); check(base!=nullptr,SDL_GetError());
    std::filesystem::path assets=std::filesystem::path(base)/"assets"; SDL_free(base);
    auto texture=[&](const char* name,VkFormat format) {
        int width,height,channels;
        stbi_uc* pixels=stbi_load((assets/name).string().c_str(),&width,&height,&channels,4);
        check(pixels!=nullptr,"Cannot read packaged Earth textures; keep assets beside renderer.exe");
        AllocatedImage image;
        try { image=create_image(pixels,{uint32_t(width),uint32_t(height),1},format,VK_IMAGE_USAGE_SAMPLED_BIT,true); }
        catch (...) { stbi_image_free(pixels); throw; }
        stbi_image_free(pixels);
        return image;
    };
    auto stopwatch=std::chrono::steady_clock::now();
    auto lap=[&] { auto now=std::chrono::steady_clock::now(); double s=std::chrono::duration<double>(now-stopwatch).count(); stopwatch=now; return s; };
    _world=_worldFuture.valid() ? _worldFuture.get() : generate_world(8192,4096);
    double worldSeconds=lap();
    _earthTexture=create_image(_world.pixels.data(),{_world.width,_world.height,1},VK_FORMAT_R8G8B8A8_SRGB,VK_IMAGE_USAGE_SAMPLED_BIT,true);
    _earthNormal=create_image(_world.normals.data(),{_world.width,_world.height,1},VK_FORMAT_R8G8B8A8_UNORM,VK_IMAGE_USAGE_SAMPLED_BIT,true);
    _earthLights=create_image(_world.lights.data(),{_world.width,_world.height,1},VK_FORMAT_R8G8B8A8_UNORM,VK_IMAGE_USAGE_SAMPLED_BIT,true);
    _cloudTexture=texture("earth-clouds.jpg",VK_FORMAT_R8G8B8A8_UNORM);
    fmt::print("Globe startup: waited {:.2f} s for world generation, texture upload {:.2f} s\n",worldSeconds,lap());
    using Constants=GLTFMetallic_Roughness::MaterialConstants;
    constexpr int MaterialCount=13;
    _globeMaterialBuffer=create_buffer(MaterialCount*sizeof(Constants),VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,VMA_MEMORY_USAGE_CPU_TO_GPU);
    auto* data=static_cast<Constants*>(_globeMaterialBuffer.info.pMappedData);
    for (int i=0;i<MaterialCount;++i) data[i]={};
    data[0].colorFactors={1,1,1,1}; data[0].metal_rough_factors={0,.95f,0,0}; data[0].materialFlags.x=1; data[0].materialFlags.w=1;
    data[1].materialFlags.y=1;
    data[2].materialFlags.y=2;
    data[3].colorFactors={.95f,.95f,.9f,1}; data[3].materialFlags.z=1;
    data[4].colorFactors={.1f,.75f,1,1}; data[4].materialFlags.z=1;
    data[5].colorFactors={1,.7f,.18f,1}; data[5].materialFlags.z=1;
    // Route ribbons (HDR colour so bloom picks them up; extra[0] = dashes along the arc, dash speed).
    data[6].colorFactors={.5f,1.2f,2.f,.3f}; data[7].colorFactors={1.f,2.4f,4.f,.75f}; data[8].colorFactors={4.f,.5f,.4f,.8f};
    for (int i=6;i<9;++i) { data[i].materialFlags.y=3; data[i].extra[0]={18,i==7 ? 1.5f : .4f,0,0}; }
    const glm::vec4 goods[]={{.45f,1.4f,.35f,1},{1.6f,.85f,.2f,1},{.6f,.8f,1.6f,1},{1.6f,.4f,.55f,1}};
    for (int i=0;i<4;++i) { data[9+i].colorFactors=goods[i]; data[9+i].materialFlags.z=1; }
    MaterialInstance* materials[]={&_sphereMaterial,&_cloudMaterial,&_atmosphereMaterial,&_planeMaterial,&_shipMaterial,&_portMaterial,
        &_routeMaterials[0],&_routeMaterials[1],&_routeMaterials[2],&_cargoMaterials[0],&_cargoMaterials[1],&_cargoMaterials[2],&_cargoMaterials[3]};
    for (int i=0;i<MaterialCount;++i) {
        GLTFMetallic_Roughness::MaterialResources r;
        r.colorImage=i==0 ? _earthTexture : i==1 ? _cloudTexture : _whiteImage;
        // The globe derives roughness from the ocean mask, so its metal/rough slot carries city lights.
        r.metalRoughImage=i==0 ? _earthLights : _whiteImage; r.normalImage=i==0 ? _earthNormal : _flatNormalImage;
        r.colorSampler=r.metalRoughSampler=r.normalSampler=_defaultSamplerLinear;
        r.dataBuffer=_globeMaterialBuffer.buffer; r.dataBufferOffset=i*sizeof(Constants);
        bool transparent=i==1 || i==2 || (i>=6 && i<9);
        *materials[i]=metalRoughMaterial.write_material(_device,transparent ? MaterialPass::Transparent : MaterialPass::MainColor,r,_descriptors);
    }
    VK_CHECK(vmaFlushAllocation(_allocator,_globeMaterialBuffer.allocation,0,VK_WHOLE_SIZE));
    for (bool plane : {false,true}) {
        std::vector<Vertex> vertices; std::vector<uint32_t> indices;
        vehicle_geometry(plane,vertices,indices);
        (plane ? _planeIndices : _shipIndices)=uint32_t(indices.size());
        (plane ? _planeMesh : _shipMesh)=uploadMesh(indices,vertices);
    }
    std::vector<Vertex> markerVertices;
    for (auto p : {glm::vec3(1,0,0),{-1,0,0},{0,1,0},{0,-1,0},{0,0,1},{0,0,-1}}) {
        Vertex v; v.position=p; v.normal=p; markerVertices.push_back(v);
    }
    std::vector<uint32_t> markerIndices{2,0,4,2,4,1,2,1,5,2,5,0,3,4,0,3,1,4,3,5,1,3,0,5};
    _portMesh=uploadMesh(markerIndices,markerVertices);
    _routes=world_demo_routes(_world);
    if (_replayPath) {
        std::ifstream input(*_replayPath);
        check(input.good(),"Cannot open replay file");
        _replay=load_replay(input,_world);
        for (const auto& route : _replay->routes) {
            _replayRoutes.push_back(cargo_route(_world,route.from,route.to,route.travelTicks,route.id));
        }
        // One ribbon per country pair; both directions share it.
        for (size_t i=0;i<_replay->routes.size();++i) {
            const auto& route=_replay->routes[i];
            if (route.from>route.to) continue;
            RouteArc arc; arc.forward=int(i);
            for (size_t j=0;j<_replay->routes.size();++j)
                if (_replay->routes[j].from==route.to && _replay->routes[j].to==route.from) arc.backward=int(j);
            std::vector<Vertex> vertices; std::vector<uint32_t> indices;
            route_ribbon(_replayRoutes[i],.009f,vertices,indices);
            arc.indexCount=uint32_t(indices.size()); arc.mesh=uploadMesh(indices,vertices);
            _routeArcs.push_back(arc);
        }
        bool planeFound = false, shipFound = false;
        for (const auto& day : _replay->days) for (const auto& shipment : day.shipments) {
            const auto& route = _replay->routes[shipment.route];
            bool plane = _replayRoutes[shipment.route].plane;
            if (plane ? planeFound : shipFound) continue;
            (plane ? _planeCaption : _shipCaption) = fmt::format("GEarth shipment: {} {}, {} -> {} (day {})", shipment.quantity,
                shipment.good, _world.countries[route.from].name, _world.countries[route.to].name, day.tick);
            (plane ? planeFound : shipFound) = true;
        }
        fmt::print("Replay: {} run {}, {} days, {} routes\n",_replay->mode,_replay->runId,_replay->days.size(),_replay->routes.size());
        if (_replay->is_snapshot()) fmt::print("Snapshot: day {}, {} shipments in transit; no historical playback\n",
            _replay->days[0].tick, _replay->days[0].shipments.size());
    } else {
        fmt::print("DEMO globe: procedural geography, 10 fictional countries, {} illustrative routes; no live simulation connection\n",_routes.size());
    }
}

void VulkanEngine::update_globe() {
    auto add=[&](GPUMeshBuffers& mesh,uint32_t count,MaterialInstance& material,const glm::mat4& matrix) {
        RenderObject object; object.indexCount=count; object.indexBuffer=mesh.indexBuffer.buffer;
        object.vertexBufferAddress=mesh.vertexBufferAddress; object.material=&material; object.transform=matrix;
        auto& surfaces=material.passType==MaterialPass::Transparent ? _drawContext.TransparentSurfaces : _drawContext.OpaqueSurfaces;
        surfaces.push_back(object);
    };
    if (_clouds) add(_sphere,_sphereIndices,_cloudMaterial,
        glm::rotate(glm::scale(glm::mat4(1),glm::vec3(1.006f)),_demoSeconds*.001f,glm::vec3(0,1,0)));
    if (_atmosphere) add(_sphere,_sphereIndices,_atmosphereMaterial,glm::scale(glm::mat4(1),glm::vec3(1.009f)));
    if (!_traffic) return;
    for (const auto& country : _world.countries) {
        glm::vec3 p=country.center*1.012f;
        add(_portMesh,24,_portMaterial,glm::scale(glm::translate(glm::mat4(1),p),glm::vec3(.006f)));
        if (country.hasPort && !_replay && !_eventReplay && !_pipeline) add(_portMesh,24,_portMaterial,
            glm::scale(glm::translate(glm::mat4(1),country.port*1.012f),glm::vec3(.004f)));
    }
    auto vehicle=[&](const DemoRoute& route,float progress,int good=-1) {
        glm::vec3 p=route_position(route,progress), up=glm::normalize(p);
        // Trail: shrinking dots behind the cargo, coloured by good.
        if (good>=0) for (int k=1;k<8 && progress-k*.015f>0;++k)
            add(_portMesh,24,_cargoMaterials[good],glm::scale(glm::translate(glm::mat4(1),route_position(route,progress-k*.015f)),
                glm::vec3(.0075f*(1-k/9.f))));
        glm::vec3 forward=route_position(route,std::min(1.f,progress+.001f))-route_position(route,std::max(0.f,progress-.001f));
        forward=glm::normalize(forward-up*glm::dot(forward,up));
        glm::vec3 right=glm::normalize(glm::cross(up,forward));
        glm::mat4 matrix(glm::vec4(right,0),glm::vec4(up,0),glm::vec4(forward,0),glm::vec4(p,1));
        matrix=glm::scale(matrix,glm::vec3(.65f));
        add(route.plane ? _planeMesh : _shipMesh,route.plane ? _planeIndices : _shipIndices,
            good>=0 ? _cargoMaterials[good] : route.plane ? _planeMaterial : _shipMaterial,matrix);
    };
    if (_pipeline) {
        const char* goods[]={"food","energy","tools","medicine"};
        std::vector<uint8_t> busy(_replayRoutes.size());
        for (const auto& pose : _pipeline->cargo_at(_pipeline->time)) {
            int good=int(std::find_if(std::begin(goods),std::end(goods),[&](const char* g) { return pose.good==g; })-std::begin(goods))%4;
            vehicle(_replayRoutes[pose.route],pose.progress,good);
            busy[pose.route]=1;
        }
        auto open=_pipeline->routes_at(_pipeline->time);
        for (auto& arc : _routeArcs) {
            bool closed=!open[arc.forward] || (arc.backward>=0 && !open[arc.backward]);
            bool carrying=busy[arc.forward] || (arc.backward>=0 && busy[arc.backward]);
            add(arc.mesh,arc.indexCount,_routeMaterials[closed ? 2 : carrying ? 1 : 0],glm::mat4(1));
        }
        return;
    }
    if (_eventReplay) {
        const char* goods[]={"food","energy","tools","medicine"};
        for (int i : _eventReplay->mappedEvents) {
            const auto& shipment=_eventReplay->events[i];
            if (!shipment.shipment() || !_eventReplay->active(i)) continue;
            auto [route,progress]=_eventReplay->shipment_progress(shipment);
            int good=int(std::find_if(std::begin(goods),std::end(goods),[&](const char* g) { return shipment.good==g; })-std::begin(goods))%4;
            vehicle(_replayRoutes[route],progress,good);
        }
        return;
    }
    if (_replay) {
        float time=replay_time(), fraction=time-std::floor(time);
        const auto& day=_replay->days[size_t(time)];
        std::vector<uint8_t> busy(_replay->routes.size());
        const char* goods[]={"food","energy","tools","medicine"};
        for (const auto& shipment : day.shipments) {
            const auto& route=_replay->routes[shipment.route];
            int good=int(std::find_if(std::begin(goods),std::end(goods),[&](const char* g) { return shipment.good==g; })-std::begin(goods))%4;
            vehicle(_replayRoutes[shipment.route],replay_progress(route.travelTicks,shipment.remaining,fraction),good);
            busy[shipment.route]=1;
        }
        for (auto& arc : _routeArcs) {
            bool open=day.routeOpen[arc.forward] && (arc.backward<0 || day.routeOpen[arc.backward]);
            bool carrying=busy[arc.forward] || (arc.backward>=0 && busy[arc.backward]);
            add(arc.mesh,arc.indexCount,_routeMaterials[!open ? 2 : carrying ? 1 : 0],glm::mat4(1));
        }
        return;
    }
    for (const auto& route : _routes) for (int trip=0;trip<2;++trip)
        vehicle(route,std::fmod(_demoSeconds/route.seconds+route.offset+trip*.5f,1.f));
}
void VulkanEngine::draw_space(VkCommandBuffer cmd) {
    vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_spacePipeline.pipeline);
    StarfieldPushConstants push;
    // Remove camera translation: star directions stay fixed at infinity while orbiting or zooming.
    push.rotationProjection=_sceneData.viewproj*glm::translate(glm::mat4(1),glm::vec3(_sceneData.cameraPosition));
    push.viewport={float(_swapchainExtent.width),float(_swapchainExtent.height),0,0};
    vkCmdPushConstants(cmd,_spacePipeline.layout,VK_SHADER_STAGE_VERTEX_BIT,0,sizeof(push),&push);
    vkCmdDraw(cmd,9000*6,1,0,0);
}

void VulkanEngine::update_ocean() {
    float t = 0, fov = 62;
    glm::vec3 eye, forward, up;
    auto rotated = [](glm::vec3 v, float angle, glm::vec3 axis) { return glm::vec3(glm::rotate(glm::mat4(1), angle, axis)*glm::vec4(v,0)); };
    if (_oceanView == OceanView::Plane) {
        // Low flyover at 150 m keeps individual swells visible through the cockpit.
        constexpr float Speed = 110;
        t = std::fmod(_oceanSeconds, 600.f);
        eye = {400*std::sin(t*.02f), 150+12*std::sin(t*.3f), Speed*t};
        forward = glm::normalize(glm::vec3(8*std::cos(t*.02f), (_vehicles[0].model ? -.06f : -.17f)*Speed, Speed));
        up = rotated({0,1,0}, .1f*std::sin(t*.05f), forward);
        fov = 55;
    } else if (_oceanView == OceanView::Meadow) {
        // Walk through the meadow at 1.5 m/s along a lazily winding path. The eye follows the ground averaged over
        // the last and next few metres, so small rises don't jolt the view; a slight bob reads as walking.
        t = std::fmod(_oceanSeconds, 1800.f);
        auto path = [](float s) { return glm::vec2(40*std::sin(s*.011f) + 12*std::sin(s*.037f), 1.5f*s); };
        glm::vec2 at = path(t), ahead = path(t+2), behind = path(t-2);
        float ground = 0;
        for (float s : {-2.f, -1.f, 0.f, 1.f, 2.f}) ground += meadow_height(path(t+s)) / 5;
        glm::vec2 heading = glm::normalize(ahead - behind);
        float slope = (meadow_height(ahead) - meadow_height(behind)) / glm::length(ahead - behind);
        eye = {at.x, ground + 1.55f + .025f*std::sin(t*5.6f), at.y};
        forward = glm::normalize(glm::vec3(heading.x, slope*.35f, heading.y));
        up = {0, 1, 0};
        fov = 70;
    } else {
        // Cargo ship: at the bow, 59.25 m ahead of the hull centre and 9.75 m above its waterline. Motion follows the
        // sea height under a 120 m by 18 m hull, so the ship moves with the waves the viewer sees.
        constexpr float Speed = 9;
        t = std::fmod(_oceanSeconds, 1800.f);
        float yaw = .08f*std::sin(t*.05f);
        glm::vec3 heading{std::sin(yaw), 0, std::cos(yaw)}, side = glm::normalize(glm::cross(heading, glm::vec3(0,1,0)));
        glm::vec2 centre{20*std::sin(t*.05f), Speed*t}, along{heading.x, heading.z}, across{side.x, side.z};
        auto sea = [&](glm::vec2 at) { return sea_displacement(at, t).y; };
        float bow = sea(centre+along*60.f), stern = sea(centre-along*60.f), port = sea(centre-across*9.f), starboard = sea(centre+across*9.f);
        float pitch = std::atan((bow-stern)/120.f), roll = std::atan((port-starboard)/18.f);
        forward = rotated(heading, pitch, side);
        up = rotated(rotated({0,1,0}, pitch, side), roll, forward);
        eye = glm::vec3(centre.x, (bow+stern+port+starboard)*.25f, centre.y) + up*9.75f + forward*59.25f;
        fov = 65;
    }
    float width = float(_swapchainExtent.width), height = float(_swapchainExtent.height);
    // A carried cockpit or deck sits right in front of the lens, so those views clip much closer.
    auto projection = glm::perspective(glm::radians(fov), width/height, 40000.f, .05f);
    projection[1][1] *= -1;
    // Camera-relative rendering: everything drawn here is placed relative to the camera, which sits at the origin.
    // The flight covers tens of kilometres, and absolute positions there lose enough float precision on the GPU to
    // make a cockpit fixed to the camera jitter by fractions of a pixel (seen as flashing edges and glow).
    glm::vec3 side = glm::normalize(glm::cross(up, forward)), level = glm::cross(forward, side);
    if (Vehicle* vehicle = active_vehicle(); vehicle && vehicle->model) {
        glm::mat4 frame(glm::vec4(side,0), glm::vec4(level,0), glm::vec4(forward,0), glm::vec4(0,0,0,1));
        vehicle->model->Draw(frame*glm::translate(glm::mat4(1),vehicle->offset)*glm::rotate(glm::mat4(1),glm::radians(vehicle->yaw),glm::vec3(0,1,0))
            *glm::scale(glm::mat4(1),glm::vec3(vehicle->scale)), _drawContext);
    }
    const glm::vec2 restingLook = _oceanView == OceanView::Meadow ? glm::vec2(0, -.06f) : _oceanView == OceanView::Ship ? glm::vec2(0, -.10f) : glm::vec2(0, -.08f);
    _sceneData.viewproj = projection*head_view(forward, up, _headAngles + restingLook);
    // Low sun ahead reveals the swell through a path of reflected light in both vehicle views.
    // ~15 degrees up: low enough for a long glitter path, high enough that the sea reflects blue sky, not violet haze.
    glm::vec3 light = glm::normalize(glm::vec3(.3f, .28f, 1.f));
    // Meadow: golden-hour sun ahead and to the right of the walk, so the grass ahead glows backlit.
    if (_oceanView == OceanView::Meadow) {
        float elevation = glm::radians(_meadowSunHeight), azimuth = glm::radians(35.f);
        light = {std::sin(azimuth)*std::cos(elevation), std::sin(elevation), std::cos(azimuth)*std::cos(elevation)};
    }
    _sceneData.cameraPosition = glm::vec4(0, 0, 0, 1); _sceneData.sunlightDirection = glm::vec4(light, 0);
    // Ambient and shadows only reach the carried vehicle (the sea and sky shade themselves): sky-blue ambient scaled by
    // each vertex's baked sky visibility, and a sun shadow map fitted around the cockpit or ship.
    _sceneData.sunlightColor = glm::vec4(_sunIntensity);
    _sceneData.ambientColor = glm::vec4(glm::vec3(.85f, .92f, 1.05f) * (_sunIntensity / 3.f), 0);
    _sceneData.moonLight = {}; _sceneData.nightSettings = {};
    float reach = _oceanView == OceanView::Ship ? 90.f : 4.f;
    // The shadow map's texel grid is tied to the vehicle (its up axis follows the vehicle's forward), so banking and
    // turning don't slide texels across the cockpit and make shadow edges crawl.
    glm::vec3 lightUp = std::abs(glm::dot(light, forward)) > .99f ? level : forward;
    auto lightProjection = glm::ortho(-reach, reach, -reach, reach, .1f, reach * 6.f); lightProjection[1][1] *= -1;
    _sceneData.sunViewProjection = lightProjection * glm::lookAt(light * reach * 3.f, glm::vec3(0), lightUp);
    _sceneData.shadowSettings = {_vehicleShadows ? 1.f : 0.f, 1.f/ShadowResolution, .0004f, reach * .002f};
    float windHeading = glm::radians(_windHeading);
    _oceanPush = {glm::inverse(_sceneData.viewproj), {t, eye.x, eye.z, eye.y}, {width, height, 0, 0}};
    if (_oceanView == OceanView::Meadow)
        _oceanPush.wind = {std::sin(windHeading)*_windStrength, std::cos(windHeading)*_windStrength, _gustiness, 2*std::tan(glm::radians(fov)/2)/height};
    // The sky's sun already blooms; a screen-space glare would shine through the cockpit or cargo.
    _sunGlare = {};
}
void VulkanEngine::draw_ocean(VkCommandBuffer cmd) {
    VkDescriptorSet scene = _frames[_frameNumber%FRAME_OVERLAP].sceneDescriptor;
    bool meadow = _oceanView == OceanView::Meadow;
    for (auto* pipeline : {&_skyPipeline, meadow ? &_terrainPipeline : &_waterPipeline}) {
        vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline->pipeline);
        vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline->layout,0,1,&scene,0,nullptr);
        vkCmdPushConstants(cmd,pipeline->layout,VK_SHADER_STAGE_VERTEX_BIT|VK_SHADER_STAGE_FRAGMENT_BIT,0,sizeof(_oceanPush),&_oceanPush);
        vkCmdDraw(cmd,pipeline == &_skyPipeline ? 3 : 290*360*6,1,0,0); // water.vert / terrain.vert: RINGS*SEGMENTS*6
    }
    if (!meadow) return;
    // Grass: one draw per band, nearest first so dense near blades fill depth before far ones; band = first instance.
    // The last band is wildflowers.
    vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_grassPipeline.pipeline);
    vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_grassPipeline.layout,0,1,&scene,0,nullptr);
    vkCmdPushConstants(cmd,_grassPipeline.layout,VK_SHADER_STAGE_VERTEX_BIT|VK_SHADER_STAGE_FRAGMENT_BIT,0,sizeof(_oceanPush),&_oceanPush);
    for (uint32_t band = 0; band < 4; ++band) {
        uint32_t side = 2 * uint32_t(std::ceil(GrassBands[band].outer / GrassBands[band].spacing));
        vkCmdDraw(cmd,side*side*GrassBands[band].verts,1,0,band);
    }
}
void VulkanEngine::load_vehicle(int index) {
    auto& vehicle = _vehicles[index];
    const auto& path = _vehiclePaths[index];
    if (vehicle.model || !path) return;
    auto loaded = loadGltf(this, *path, true); check(loaded.has_value(), "Vehicle import failed"); vehicle.model = *loaded;
    // Bundled models that need a different placement from the slot default (found by probing each model).
    if (path->parent_path().filename() == "boeing_737_cockpit") { vehicle.offset = {-.25f, -2.20f, -1.25f}; vehicle.scale = 1; vehicle.yaw = 0; }
    if (path->parent_path().filename() == "free_plane_cockpit") { vehicle.offset = {-1.041f, -1.422f, 1.174f}; vehicle.scale = .0254f; vehicle.yaw = 0; }
    fmt::print("Vehicle model loaded: {}\n", path->string());
}
void VulkanEngine::enter_ground() {
    if (!_townMesh.indexBuffer.buffer) {
        std::vector<Vertex> vertices; std::vector<uint32_t> indices;
        town_geometry(_world,vertices,indices); _townMesh=uploadMesh(indices,vertices);
    }
    _town={};
    if (_pipeline) {
        size_t f=_pipeline->frame_index(_pipeline->time);
        _town=town_from_snapshot(_pipeline->frames[f].snapshot,f ? std::string_view(_pipeline->frames[f-1].snapshot) : std::string_view(),
            _world.countries[_townCountry].id,24);
    }
    if (_town.people.empty()) _town=demo_town(16);
    _scene=Scene::Ground; _townYaw=.6f; _townDistance=24;
}
void VulkanEngine::update_ground() {
    _town.draw(_townCountry,[&](int color,const glm::mat4& matrix) {
        RenderObject object; object.indexCount=36; object.firstIndex=uint32_t(color*36); object.indexBuffer=_townMesh.indexBuffer.buffer;
        object.vertexBufferAddress=_townMesh.vertexBufferAddress; object.material=&_townMaterial; object.transform=matrix;
        _drawContext.OpaqueSurfaces.push_back(object);
    });
    // Tilted top-down camera: steep enough to take in the square, shallow enough to see legs move.
    float pitch=glm::radians(52.f);
    glm::vec3 target(0,.8f,0), eye=target+_townDistance*glm::vec3(std::cos(pitch)*std::sin(_townYaw),std::sin(pitch),std::cos(pitch)*std::cos(_townYaw));
    auto projection=glm::perspective(glm::radians(40.f),float(_swapchainExtent.width)/_swapchainExtent.height,2000.f,.1f);
    projection[1][1]*=-1;
    _sceneData.viewproj=projection*glm::lookAt(eye,target,glm::vec3(0,1,0));
    glm::vec3 light=glm::normalize(glm::vec3(.5f,.8f,.35f));
    _sceneData.cameraPosition=glm::vec4(eye,1); _sceneData.sunlightDirection=glm::vec4(light,0);
    _sceneData.sunlightColor=glm::vec4(_sunIntensity); _sceneData.ambientColor=glm::vec4(glm::vec3(.85f,.92f,1.05f)*.35f,0);
    _sceneData.moonLight={}; _sceneData.nightSettings={}; _sceneData.lightInfo={};
    auto lightProjection=glm::ortho(-32.f,32.f,-32.f,32.f,.1f,120.f); lightProjection[1][1]*=-1;
    _sceneData.sunViewProjection=lightProjection*glm::lookAt(light*60.f,glm::vec3(0),glm::vec3(0,1,0));
    _sceneData.shadowSettings={1.f,1.f/ShadowResolution,.0006f,.02f};
    _oceanPush.inverseViewProj=glm::inverse(_sceneData.viewproj);
    _oceanPush.viewport={float(_swapchainExtent.width),float(_swapchainExtent.height),0,0}; _oceanPush.wind={};
    _sunGlare={};
}
void VulkanEngine::update_scene() {
    _drawContext.OpaqueSurfaces.clear(); _drawContext.TransparentSurfaces.clear();
    if (_scene == Scene::Ocean) { update_ocean(); return; }
    if (_scene == Scene::Ground) { update_ground(); return; }
    auto append = [&](GPUMeshBuffers& mesh, uint32_t count, MaterialInstance& material) {
        RenderObject object; object.indexCount = count; object.indexBuffer = mesh.indexBuffer.buffer;
        object.vertexBufferAddress = mesh.vertexBufferAddress; object.material = &material;
        _drawContext.OpaqueSurfaces.push_back(object);
    };
    if (_model) _model->Draw(_modelTransform,_drawContext); else append(_sphere,_sphereIndices,_sphereMaterial);
    if (_globe) update_globe();
    if (_showGround) append(_ground,_groundIndices,_groundMaterial);
    glm::vec3 eye = _target + _distance * glm::vec3(std::cos(_pitch)*std::sin(_yaw),std::sin(_pitch),std::cos(_pitch)*std::cos(_yaw));
    auto view = glm::lookAt(eye,_target,glm::vec3(0,1,0));
    auto projection = glm::perspective(glm::radians(45.f),float(_swapchainExtent.width)/_swapchainExtent.height,100.f,.05f);
    if (_environment) {
        eye=_cameraPosition;
        view=glm::lookAt(eye,eye+view_forward(_cameraAngles),glm::vec3(0,1,0));
        projection=glm::perspective(glm::radians(60.f),float(_swapchainExtent.width)/_swapchainExtent.height,1000.f,.05f);
    }
    projection[1][1] *= -1; _sceneData.viewproj = projection * view;
    // Globe: the sun is fixed relative to the camera (x right, y up, z toward viewer), so the same side stays lit while orbiting.
    glm::vec3 toCamera = glm::normalize(eye-_target), right = glm::normalize(glm::cross(glm::vec3(0,1,0),toCamera));
    glm::mat3 viewBasis(right,glm::cross(toCamera,right),toCamera);
    glm::vec3 light = glm::normalize(_globe ? viewBasis*_sunDirection : _sunDirection);
    float globeAngle = std::asin(std::min(1.f,1.f/glm::length(eye)));
    if (_globe && _night) {
        // Night view: the sun sits just behind the upper-right limb at any zoom, backlighting a thin crescent.
        float behind = globeAngle + glm::radians(3.f);
        light = glm::normalize(viewBasis*glm::vec3(std::sin(behind)*.7071f,std::sin(behind)*.7071f,-std::cos(behind)));
    }
    glm::vec3 up = std::abs(light.y) > .99f ? glm::vec3(0,0,1) : glm::vec3(0,1,0);
    auto lightView = glm::lookAt(light * 6.f,glm::vec3(0),up);
    auto lightProjection = glm::ortho(-3.5f,3.5f,-3.5f,3.5f,.1f,14.f); lightProjection[1][1] *= -1;
    _sceneData.sunViewProjection = lightProjection * lightView;
    if (_environment) {
        glm::vec3 center=eye+view_forward(_cameraAngles)*15.f;
        float texel=70.f/ShadowResolution;
        glm::mat4 rotation=glm::lookAt(light*100.f,glm::vec3(0),up);
        glm::vec3 snapped=glm::vec3(rotation*glm::vec4(center,1));
        snapped.x=std::floor(snapped.x/texel)*texel; snapped.y=std::floor(snapped.y/texel)*texel;
        center=glm::vec3(glm::inverse(rotation)*glm::vec4(snapped,1));
        auto projection=glm::ortho(-35.f,35.f,-35.f,35.f,.1f,220.f); projection[1][1]*=-1;
        _sceneData.sunViewProjection=projection*glm::lookAt(center+light*110.f,center,up);
        _oceanPush.inverseViewProj=glm::inverse(_sceneData.viewproj);
        _oceanPush.viewport={float(_swapchainExtent.width),float(_swapchainExtent.height),0,0}; _oceanPush.wind={};
    }
    _sceneData.cameraPosition = glm::vec4(eye,1); _sceneData.sunlightDirection = glm::vec4(light,0);
    float sun = _sunIntensity;
    _sceneData.sunlightColor = glm::vec4(sun);
    // Night view: cool moonlight keeps land, ice and coasts faintly readable.
    _sceneData.ambientColor = _night ? glm::vec4(.015f,.02f,.03f,0) : glm::vec4(_ambient);
    // Moonlight from the camera's upper left, so the side being watched is the side that is lit.
    _sceneData.moonLight = _globe && _night ? glm::vec4(glm::normalize(viewBasis*glm::vec3(-.45f,.45f,.77f)),_moonIntensity) : glm::vec4(0);
    _sunGlare = {};
    if (_globe) {
        glm::vec4 clip = _sceneData.viewproj*glm::vec4(eye+light*10.f,1);
        float fromCenter = std::acos(std::clamp(glm::dot(light,glm::normalize(_target-eye)),-1.f,1.f));
        float visible = glm::smoothstep(globeAngle-.01f,globeAngle+.03f,fromCenter);
        if (clip.w > 0 && visible > 0)
            _sunGlare = {clip.x/clip.w*.5f+.5f,clip.y/clip.w*.5f+.5f,visible*sun/3.f,float(_swapchainExtent.width)/_swapchainExtent.height};
    }
    _sceneData.nightSettings = {_cityLightIntensity, std::min(sun/1.5f,1.f), _pipeline ? _pipeline->time : _replay ? replay_time() : std::fmod(_demoSeconds,1000.f), 0};
    if (_globe) {
        // Replay: brighter for bigger, more employed countries; a demo globe lights every country equally.
        float meanResidents = 0;
        const Replay::Day* day = _replay ? &_replay->days[size_t(replay_time())] : nullptr;
        const auto* nations=_pipeline ? &_pipeline->frames[_pipeline->frame_index(_pipeline->time)].nations : day ? &day->nations : nullptr;
        if (nations) for (const auto& nation : *nations) meanResidents += nation.residents / float(FictionalCountryCount);
        for (int i = 0; i < FictionalCountryCount; ++i) {
            float brightness = 1;
            if (nations) {
                const auto& nation = (*nations)[i];
                float employed = 1 - float(nation.unemployed) / std::max(nation.residents, 1);
                brightness = std::clamp(std::sqrt(nation.residents / std::max(meanResidents, 1.f)) * employed * employed * 1.3f, .15f, 1.8f);
            }
            _sceneData.cityLights[i] = glm::vec4(_world.countries[i].center, brightness);
        }
    }
    _sceneData.shadowSettings = {_shadows ? 1.f : 0.f,1.f/ShadowResolution,.0006f,.004f};
    _sceneData.lightInfo=_environment ? glm::vec4(float(_model->lights.size()),_lightExposure,1,0) : glm::vec4(0);
    if (_environment) _sceneData.shadowSettings.w=.02f;
    std::sort(_drawContext.TransparentSurfaces.begin(),_drawContext.TransparentSurfaces.end(),[&](const auto& a,const auto& b) {
        glm::vec3 pa(a.transform*glm::vec4(a.bounds.origin,1)), pb(b.transform*glm::vec4(b.bounds.origin,1));
        return glm::dot(pa-eye,pa-eye) > glm::dot(pb-eye,pb-eye);
    });
}

void VulkanEngine::draw_shadow(VkCommandBuffer cmd) {
    vkutil::transition_image(cmd,_shadowImage.image,VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL,VK_IMAGE_ASPECT_DEPTH_BIT);
    auto depth = vkinit::depth_attachment_info(_shadowImage.imageView,VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL);
    depth.clearValue.depthStencil.depth = 1;
    auto rendering = vkinit::rendering_info({ShadowResolution,ShadowResolution},nullptr,&depth);
    vkCmdBeginRendering(cmd,&rendering); set_viewport(cmd,{ShadowResolution,ShadowResolution});
    if (_shadows || _scene == Scene::Ground) {
        vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_shadowPipeline.pipeline);
        for (const auto& object : _drawContext.OpaqueSurfaces) {
            if (_environment && !surface_visible(_sceneData.sunViewProjection*object.transform,object.bounds)) continue;
            vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_shadowPipeline.layout,0,1,&object.material->materialSet,0,nullptr);
            vkCmdBindIndexBuffer(cmd,object.indexBuffer,0,VK_INDEX_TYPE_UINT32);
            GPUDrawPushConstants push{_sceneData.sunViewProjection*object.transform,object.vertexBufferAddress};
            vkCmdPushConstants(cmd,_shadowPipeline.layout,VK_SHADER_STAGE_VERTEX_BIT,0,sizeof(push),&push);
            vkCmdDrawIndexed(cmd,object.indexCount,1,object.firstIndex,0,0);
        }
    }
    vkCmdEndRendering(cmd);
    vkutil::transition_image(cmd,_shadowImage.image,VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,VK_IMAGE_ASPECT_DEPTH_BIT);
}
void VulkanEngine::draw_geometry(VkCommandBuffer cmd) {
    vkutil::transition_image(cmd,_drawImage.image,VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
    vkutil::transition_image(cmd,_depthImage.image,VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL,VK_IMAGE_ASPECT_DEPTH_BIT);
    VkClearValue clear{}; clear.color = {{.015f,.02f,.03f,1}};
    if (_globe && _spaceBackground) clear.color={{.001f,.0015f,.003f,1}};
    auto color = vkinit::attachment_info(_drawImage.imageView,&clear);
    auto depth = vkinit::depth_attachment_info(_depthImage.imageView,VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL);
    auto rendering = vkinit::rendering_info(_swapchainExtent,&color,&depth);
    vkCmdBeginRendering(cmd,&rendering); set_viewport(cmd,_swapchainExtent);
    if (_scene == Scene::Ocean) draw_ocean(cmd);
    else if (_environment || _scene == Scene::Ground) {
        vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_skyPipeline.pipeline);
        vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,_skyPipeline.layout,0,1,&_frames[_frameNumber%FRAME_OVERLAP].sceneDescriptor,0,nullptr);
        vkCmdPushConstants(cmd,_skyPipeline.layout,VK_SHADER_STAGE_VERTEX_BIT|VK_SHADER_STAGE_FRAGMENT_BIT,0,sizeof(_oceanPush),&_oceanPush);
        vkCmdDraw(cmd,3,1,0,0);
    }
    else if (_globe && _spaceBackground) draw_space(cmd);
    _visibleSurfaces=0;
    auto draw = [&](const auto& objects) {
        for (const auto& object : objects) {
            if (_environment && !surface_visible(_sceneData.viewproj*object.transform,object.bounds)) continue;
            ++_visibleSurfaces;
            auto& pipeline = *object.material->pipeline;
            vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline.pipeline);
            VkDescriptorSet sets[] = {_frames[_frameNumber%FRAME_OVERLAP].sceneDescriptor,object.material->materialSet};
            vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline.layout,0,2,sets,0,nullptr);
            vkCmdBindIndexBuffer(cmd,object.indexBuffer,0,VK_INDEX_TYPE_UINT32);
            GPUDrawPushConstants push{object.transform,object.vertexBufferAddress};
            vkCmdPushConstants(cmd,pipeline.layout,VK_SHADER_STAGE_VERTEX_BIT,0,sizeof(push),&push);
            vkCmdDrawIndexed(cmd,object.indexCount,1,object.firstIndex,0,0);
        }
    };
    draw(_drawContext.OpaqueSurfaces); draw(_drawContext.TransparentSurfaces);
    vkCmdEndRendering(cmd);
    vkutil::transition_image(cmd,_drawImage.image,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
}
void VulkanEngine::draw_bloom(VkCommandBuffer cmd) {
    VkExtent2D half{std::max(1u,_swapchainExtent.width/2),std::max(1u,_swapchainExtent.height/2)};
    vkutil::transition_image(cmd,_drawImage.image,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    vkutil::transition_image(cmd,_bloomImage.image,VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    vkutil::copy_image_to_image(cmd,_drawImage.image,_bloomImage.image,_swapchainExtent,half);
    vkutil::transition_image(cmd,_drawImage.image,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
    vkutil::generate_mipmaps(cmd,_bloomImage.image,half);
}
void VulkanEngine::draw_post(VkCommandBuffer cmd, bool fxaa) {
    auto& target = fxaa ? _finalImage : _tonemapImage;
    auto& pipeline = fxaa ? _fxaaPipeline : _tonemapPipeline;
    VkDescriptorSet set = fxaa ? _fxaaSet : _tonemapSet;
    vkutil::transition_image(cmd,target.image,VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
    auto color = vkinit::attachment_info(target.imageView,nullptr);
    auto rendering = vkinit::rendering_info(_swapchainExtent,&color,nullptr);
    vkCmdBeginRendering(cmd,&rendering); set_viewport(cmd,_swapchainExtent);
    vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline.pipeline);
    vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline.layout,0,1,&set,0,nullptr);
    // Inspect / back transition: blow the exposure out to white around the scene swap at .9 s.
    float flash = _transition < 0 ? 0.f : _transition < .9f ? glm::smoothstep(.3f,.9f,_transition) : 1-glm::smoothstep(.9f,1.5f,_transition);
    glm::vec4 push[] = {{_exposure*std::exp(7*flash),0,0,_bloom},_sunGlare};
    if (fxaa) {
        push[0] = {1.f/_swapchainExtent.width,1.f/_swapchainExtent.height,.125f,.5f};
        push[1] = {1.f-.5f/_swapchainExtent.width,1.f-.5f/_swapchainExtent.height,0,0};
    }
    vkCmdPushConstants(cmd,pipeline.layout,VK_SHADER_STAGE_FRAGMENT_BIT,0,32,push);
    vkCmdDraw(cmd,3,1,0,0); vkCmdEndRendering(cmd);
    vkutil::transition_image(cmd,target.image,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
}

void VulkanEngine::draw() {
    auto& frame = _frames[_frameNumber%FRAME_OVERLAP];
    VK_CHECK(vkWaitForFences(_device,1,&frame._renderFence,VK_TRUE,UINT64_MAX));
    uint32_t index;
    VkResult acquire = vkAcquireNextImageKHR(_device,_swapchain,UINT64_MAX,frame._swapchainSemaphore,VK_NULL_HANDLE,&index);
    if (acquire == VK_ERROR_OUT_OF_DATE_KHR) { _resize = true; return; }
    if (acquire == VK_SUBOPTIMAL_KHR) _resize = true; else VK_CHECK(acquire);
    VK_CHECK(vkResetFences(_device,1,&frame._renderFence));
    VK_CHECK(vkResetCommandBuffer(frame._mainCommandBuffer,0));
    update_scene(); std::memcpy(frame.sceneBuffer.info.pMappedData,&_sceneData,sizeof(_sceneData));
    VK_CHECK(vmaFlushAllocation(_allocator,frame.sceneBuffer.allocation,0,VK_WHOLE_SIZE));
    auto cmd = frame._mainCommandBuffer;
    auto begin = vkinit::command_buffer_begin_info(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT);
    VK_CHECK(vkBeginCommandBuffer(cmd,&begin));
    draw_shadow(cmd); draw_geometry(cmd); draw_bloom(cmd); draw_post(cmd,false); if (_fxaa) draw_post(cmd,true);
    auto& image = _fxaa ? _finalImage : _tonemapImage;
    vkutil::transition_image(cmd,image.image,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
    {
        auto color = vkinit::attachment_info(image.imageView,nullptr);
        auto rendering = vkinit::rendering_info(_swapchainExtent,&color,nullptr);
        vkCmdBeginRendering(cmd,&rendering); ImGui_ImplVulkan_RenderDrawData(ImGui::GetDrawData(),cmd); vkCmdEndRendering(cmd);
    }
    vkutil::transition_image(cmd,image.image,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    vkutil::transition_image(cmd,_swapchainImages[index],VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    vkutil::copy_image_to_image(cmd,image.image,_swapchainImages[index],_swapchainExtent,_swapchainExtent);
    vkutil::transition_image(cmd,_swapchainImages[index],VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
    VK_CHECK(vkEndCommandBuffer(cmd));
    auto command = vkinit::command_buffer_submit_info(cmd);
    auto wait = vkinit::semaphore_submit_info(VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,frame._swapchainSemaphore);
    auto signal = vkinit::semaphore_submit_info(VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,_swapchainRenderSemaphores[index]);
    auto submit = vkinit::submit_info(&command,&signal,&wait);
    VK_CHECK(vkQueueSubmit2(_graphicsQueue,1,&submit,frame._renderFence));
    VkPresentInfoKHR present{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
    present.swapchainCount=1; present.pSwapchains=&_swapchain; present.pImageIndices=&index;
    present.waitSemaphoreCount=1; present.pWaitSemaphores=&_swapchainRenderSemaphores[index];
    VkResult result = vkQueuePresentKHR(_graphicsQueue,&present);
    if (result == VK_ERROR_OUT_OF_DATE_KHR || result == VK_SUBOPTIMAL_KHR) _resize = true; else VK_CHECK(result);
    ++_frameNumber;
}

void VulkanEngine::init_imgui() {
    ImGui::CreateContext(); ImGui::GetIO().IniFilename = nullptr;
    ImGui::GetIO().ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    ImGui::StyleColorsDark(); ImGui_ImplSDL2_InitForVulkan(_window);
    ImGui_ImplVulkan_InitInfo info{};
    info.ApiVersion=VK_API_VERSION_1_3; info.Instance=_instance; info.PhysicalDevice=_chosenGPU; info.Device=_device;
    info.QueueFamily=_graphicsQueueFamily; info.Queue=_graphicsQueue;
    info.DescriptorPoolSize=64; info.MinImageCount=2; info.ImageCount=uint32_t(_swapchainImages.size()); info.UseDynamicRendering=true;
    info.PipelineInfoMain.PipelineRenderingCreateInfo={VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO};
    info.PipelineInfoMain.PipelineRenderingCreateInfo.colorAttachmentCount=1;
    info.PipelineInfoMain.PipelineRenderingCreateInfo.pColorAttachmentFormats=&_tonemapImage.imageFormat;
    info.PipelineInfoMain.MSAASamples=VK_SAMPLE_COUNT_1_BIT;
    check(ImGui_ImplVulkan_Init(&info),"ImGui Vulkan initialization failed"); _imguiInitialized=true;
}
void VulkanEngine::frame_ui() {
    ImGui_ImplVulkan_NewFrame(); ImGui_ImplSDL2_NewFrame(); ImGui::NewFrame();
    if (_showPanels) {
        if (_globe) {
            ImGui::SetNextWindowPos(ImVec2(18,365),ImGuiCond_Always);
            ImGui::SetNextWindowSize(ImVec2(260,std::max(180.f,ImGui::GetIO().DisplaySize.y-383)),ImGuiCond_Always);
        }
        if (_environment) ImGui::SetNextWindowSize(ImVec2(320,std::min(520.f,ImGui::GetIO().DisplaySize.y-36)),ImGuiCond_FirstUseEver);
        ImGui::Begin("Lighting",nullptr,_globe ? ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoResize : 0);
        ImGui::PushItemWidth(115);
        if (_environment) {
            ImGui::TextUnformatted("Environment preview");
            ImGui::TextWrapped("Hold right mouse: look and WASD move. Q/E down/up. Shift: faster. R: reset camera.");
            ImGui::SliderFloat("Movement speed",&_cameraSpeed,.5f,30.f);
            ImGui::SliderFloat("Local lights",&_lightExposure,0.f,.1f,"%.3f");
            ImGui::Text("%zu / %zu surfaces | %.0f MiB textures",_visibleSurfaces,
                _drawContext.OpaqueSurfaces.size()+_drawContext.TransparentSurfaces.size(),_ddsBytes/1048576.);
            ImGui::TextWrapped("Static scene; glass and indirect lighting approximate.");
        }
        if (_globe) {
            int scene = int(_scene);
            ImGui::RadioButton("Globe",&scene,0); ImGui::SameLine(); ImGui::RadioButton("Plane / ship",&scene,1);
            if (scene != int(_scene)) { set_mouse_look(false); _headAngles = {}; }
            _scene = Scene(scene);
        }
        if (_globe && _scene == Scene::Globe) {
            if (_pipeline) {
                ImGui::TextUnformatted("GEarth | recorded simulation");
                ImGui::TextWrapped("Playback and checkpoint details are in the simulation panel.");
            } else if (_eventReplay) {
                ImGui::TextUnformatted("REPLAY | recorded events");
                ImGui::TextWrapped("Recorded cargo and country events. Playback controls are in the event panel.");
            } else if (_replay) {
                ImGui::Text("%s | %s simulation run",_replay->is_snapshot() ? "SNAPSHOT" : "REPLAY",_replay->mode.c_str());
                ImGui::TextWrapped(_replay->is_snapshot() ? "Single recorded state. Historical playback is unavailable."
                    : _replay->mode=="RULE" ? "Rule mode: decisions are deterministic, not model behavior. Shipments as planes/ships."
                    : "Shipments in transit as planes and ships.");
            } else {
                ImGui::TextUnformatted("DEMO | illustrative traffic");
                ImGui::TextWrapped("White planes, cyan ships, gold terminals.");
            }
            ImGui::Checkbox("Clouds",&_clouds); ImGui::Checkbox("Atmosphere",&_atmosphere); ImGui::Checkbox("Stars",&_spaceBackground);
            if (!_eventReplay && !_pipeline) {
                ImGui::Checkbox("Traffic",&_traffic);
                if (!_replay || !_replay->is_snapshot()) {
                    ImGui::Checkbox("Play traffic",&_playing);
                    ImGui::SliderFloat(_replay ? "Days per second" : "Traffic speed",&_trafficSpeed,.1f,_replay ? 20.f : 3.f);
                }
            }
            ImGui::Separator();
        }
        if (_scene == Scene::Ocean) {
            int view = int(_oceanView);
            ImGui::RadioButton("Plane",&view,0); ImGui::SameLine(); ImGui::RadioButton("Ship",&view,1); ImGui::SameLine(); ImGui::RadioButton("Meadow",&view,2);
            if (view != int(_oceanView)) { set_mouse_look(false); _headAngles = {}; }
            _oceanView = OceanView(view);
            ImGui::TextWrapped("Hold right mouse to look around; release for controls. R resets the view.");
            if (ImGui::Button("Reset view (R)")) _headAngles = {};
            ImGui::Checkbox("Play",&_playing);
            if (_oceanView == OceanView::Meadow) {
                ImGui::SliderFloat("Wind",&_windStrength,0.f,1.5f);
                ImGui::SliderFloat("Gusts",&_gustiness,0.f,1.f);
                ImGui::SliderFloat("Wind heading",&_windHeading,0.f,360.f,"%.0f deg");
                ImGui::SliderFloat("Sun height",&_meadowSunHeight,2.f,60.f,"%.0f deg");
            }
            if (Vehicle* vehicle = active_vehicle(); vehicle && vehicle->model) {
                ImGui::DragFloat3("Model offset",&vehicle->offset.x,.05f,-300.f,300.f);
                ImGui::SliderFloat("Model scale",&vehicle->scale,.001f,100.f,"%.3f",ImGuiSliderFlags_Logarithmic);
                ImGui::SliderFloat("Model yaw",&vehicle->yaw,-180.f,180.f);
                ImGui::TextWrapped("offset %.2f %.2f %.2f  scale %.3f  yaw %.0f",vehicle->offset.x,vehicle->offset.y,vehicle->offset.z,vehicle->scale,vehicle->yaw);
            } else if (vehicle) ImGui::TextWrapped(_oceanView == OceanView::Ship ? "Add a ship with --ship-model" : "Add a cockpit with --plane-model");
        } else if (!_environment) ImGui::TextWrapped("Drag left mouse to orbit; wheel to zoom.");
        ImGui::SliderFloat("Exposure",&_exposure,.1f,3.f);
        ImGui::SliderFloat("Bloom",&_bloom,0.f,.3f);
        if (_globe && _scene == Scene::Globe) {
            ImGui::Checkbox("Night view (sun behind planet)",&_night);
            ImGui::SliderFloat("City lights",&_cityLightIntensity,0.f,12.f);
            if (_night) ImGui::SliderFloat("Moonlight",&_moonIntensity,0.f,1.5f);
        }
        ImGui::SliderFloat("Sun intensity",&_sunIntensity,0.f,8.f);
        ImGui::SliderFloat("Ambient fill",&_ambient,0.f,.8f);
        ImGui::SliderFloat("Sun azimuth X",&_sunDirection.x,-1.f,1.f);
        ImGui::SliderFloat("Sun azimuth Z",&_sunDirection.z,-1.f,1.f);
        ImGui::Checkbox("Shadows",_scene == Scene::Ocean ? &_vehicleShadows : &_shadows); ImGui::Checkbox("FXAA",&_fxaa); ImGui::Checkbox("Ground",&_showGround);
        ImGui::Text("%.1f FPS  |  H hides panels",ImGui::GetIO().Framerate);
        ImGui::PopItemWidth();
        ImGui::End();
    }
    if (_pipeline && _showPanels && _scene==Scene::Globe) _pipeline->draw(_world);
    if (_showPanels && _globe && _traffic && !_eventReplay && !_pipeline && _scene == Scene::Globe) {
        ImGui::SetNextWindowPos(ImVec2(std::max(18.f,ImGui::GetIO().DisplaySize.x-278),18),ImGuiCond_Always);
        ImGui::SetNextWindowSize(ImVec2(260,std::min(480.f,ImGui::GetIO().DisplaySize.y-36)),ImGuiCond_Always);
        if (_replay) {
            ImGui::Begin(_replay->is_snapshot() ? "Simulation snapshot" : "Simulation replay",nullptr,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoResize);
            ImGui::TextWrapped("%s | seed %d",_replay->runId.c_str(),_replay->seed);
            float time=replay_time();
            const auto& day=_replay->days[size_t(time)];
            if (_replay->is_snapshot()) ImGui::Text("Day %d | %s",day.tick,_replay->status.c_str());
            else {
                ImGui::PushItemWidth(-1);
                if (ImGui::SliderFloat("##day",&time,0.f,float(_replay->days.size())-.001f,"Day %.0f")) _demoSeconds=time;
                ImGui::PopItemWidth();
            }
            ImGui::Text("%zu shipments in transit:",day.shipments.size());
            const char* goods[]={"food","energy","tools","medicine"};
            const ImVec4 colors[]={{.45f,1,.35f,1},{1,.6f,.2f,1},{.55f,.7f,1,1},{1,.4f,.5f,1}};
            for (int i=0;i<4;++i) { if (i) ImGui::SameLine(); ImGui::TextColored(colors[i],"%s",goods[i]); }
            ImGui::TextColored(ImVec4(1,.4f,.35f,1),"red route = border closed");
            if (ImGui::BeginTable("nations",3,ImGuiTableFlags_SizingStretchProp)) {
                ImGui::TableSetupColumn("Country"); ImGui::TableSetupColumn("Jobless"); ImGui::TableSetupColumn("Approval");
                ImGui::TableHeadersRow();
                for (int i=0;i<FictionalCountryCount;++i) {
                    const auto& nation=day.nations[i];
                    ImGui::TableNextRow(); ImGui::TableNextColumn(); ImGui::TextUnformatted(_world.countries[i].name);
                    ImGui::TableNextColumn(); ImGui::Text("%.0f%%",100.f*nation.unemployed/std::max(nation.residents,1));
                    ImGui::TableNextColumn(); ImGui::Text("%.0f%%%s",100.f*nation.approval,nation.wars ? " war" : "");
                }
                ImGui::EndTable();
            }
            ImGui::Separator();
            int shown=0;
            for (size_t d=size_t(time)+1;d-- > 0 && shown<8;)
                for (auto event=_replay->days[d].events.rbegin();event!=_replay->days[d].events.rend() && shown<8;++event,++shown)
                    ImGui::TextWrapped("Day %d: %s",_replay->days[d].tick,event->text.c_str());
            ImGui::End();
        } else {
            ImGui::Begin("Demo movements",nullptr,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoResize);
            ImGui::TextUnformatted("Scripted example movements");
            for (const auto& route : _routes) {
                float progress=std::fmod(_demoSeconds/route.seconds+route.offset,1.f);
                ImGui::TextUnformatted(route.name.c_str()); ImGui::ProgressBar(progress,ImVec2(-1,0));
            }
            ImGui::End();
        }
    }
    if (_scene == Scene::Ocean) {
        auto* draw = ImGui::GetForegroundDrawList();
        ImVec2 size = ImGui::GetIO().DisplaySize, centre(size.x*.5f, size.y*.5f);
        auto caption = [&](const std::string& text) {
            ImVec2 textSize = ImGui::CalcTextSize(text.c_str());
            ImVec2 at((size.x-textSize.x)*.5f, size.y-textSize.y-28);
            draw->AddRectFilled(ImVec2(at.x-12,at.y-8), ImVec2(at.x+textSize.x+12,at.y+textSize.y+8), IM_COL32(0,0,0,150), 4);
            draw->AddText(at, IM_COL32(255,255,255,235), text.c_str());
        };
        if (_oceanView == OceanView::Plane) caption("ILLUSTRATIVE VIEW  |  cargo plane  |  " + _planeCaption);
        if (_oceanView == OceanView::Ship) caption("ILLUSTRATIVE VIEW  |  cargo ship  |  " + _shipCaption);
    }
    if (_scene == Scene::Ground) {
        const char* name=_pipeline ? _pipeline->names[_townCountry].c_str() : _world.countries[_townCountry].name;
        if (_showPanels) {
            ImGui::SetNextWindowPos(ImVec2(18,18),ImGuiCond_Always);
            ImGui::Begin("Market square",nullptr,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_AlwaysAutoResize);
            ImGui::Text("%s: %zu people",name,_town.people.size());
            ImGui::TextUnformatted(_town.recorded ? "Recorded agents; lines are their latest trades." : "DEMO people (no recording loaded).");
            ImGui::TextUnformatted("ILLUSTRATIVE: positions and walking are invented.");
            ImGui::TextUnformatted("Drag to turn, wheel to zoom.");
            if (ImGui::Button("Back to globe",ImVec2(-1,0)) && _transition<0) _transition=0;
            ImGui::End();
        }
        // Speech bubbles: partners take turns every two seconds of their conversation.
        auto* draw=ImGui::GetForegroundDrawList();
        ImVec2 display=ImGui::GetIO().DisplaySize;
        for (size_t i=0;i<_town.people.size();++i) {
            const auto& person=_town.people[i];
            if (person.talk<=0 || int(person.talk/2)%2!=(int(i)<person.partner ? 1 : 0)) continue;
            glm::vec4 clip=_sceneData.viewproj*glm::vec4(person.position.x,2.f,person.position.y,1);
            if (clip.w<=0) continue;
            ImVec2 tip((clip.x/clip.w*.5f+.5f)*display.x,(clip.y/clip.w*.5f+.5f)*display.y), size=ImGui::CalcTextSize(person.line.c_str());
            ImVec2 low(tip.x-size.x*.5f-8,tip.y-size.y-18), high(tip.x+size.x*.5f+8,tip.y-8);
            draw->AddRectFilled(low,high,IM_COL32(255,255,255,235),6);
            draw->AddTriangleFilled(ImVec2(tip.x-6,high.y),ImVec2(tip.x+6,high.y),tip,IM_COL32(255,255,255,235));
            draw->AddText(ImVec2(low.x+8,low.y+5),IM_COL32(25,25,25,255),person.line.c_str());
        }
    }
    if (_globe && _scene == Scene::Globe) {
        ImGui::SetNextWindowPos(ImVec2(18,18),ImGuiCond_Always);
        ImGui::SetNextWindowSize(ImVec2(260,330),ImGuiCond_Always);
        if (_showPanels) {
            ImGui::Begin("Countries",nullptr,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoResize);
            ImGui::TextUnformatted("10 fictional countries");
            for (const auto& country : _world.countries) {
                int index=int(&country-_world.countries.data());
                ImGui::PushID(index);
                ImGui::PushStyleColor(ImGuiCol_Button,ImVec4(country.color.r*.5f,country.color.g*.5f,country.color.b*.5f,1));
                const char* name=_pipeline ? _pipeline->names[index].c_str() : country.name;
                if (ImGui::Button(name,ImVec2(-70,0))) {
                    _yaw=std::atan2(country.center.x,country.center.z); _pitch=std::asin(country.center.y);
                    _selectedCountry=index; _countryInfoRevision=-1;
                }
                ImGui::PopStyleColor(); ImGui::SameLine();
                if (ImGui::Button("Inspect",ImVec2(-1,0)) && _transition<0) {
                    _yaw=std::atan2(country.center.x,country.center.z); _pitch=std::asin(country.center.y);
                    _townCountry=index; _transition=0;
                }
                ImGui::PopID();
            }
            ImGui::End();
        }
        if (_showPanels && _selectedCountry>=0) {
            int c=_selectedCountry;
            const auto& country=_world.countries[c];
            ImVec2 display=ImGui::GetIO().DisplaySize;
            ImGui::SetNextWindowPos(ImVec2(display.x*.5f,display.y-18),ImGuiCond_Always,ImVec2(.5f,1));
            bool open=true;
            ImGui::Begin("Country",&open,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoCollapse|ImGuiWindowFlags_AlwaysAutoResize);
            ImGui::TextColored(ImVec4(country.color.r,country.color.g,country.color.b,1),"%s",_pipeline ? _pipeline->names[c].c_str() : country.name);
            ImGui::SameLine(); ImGui::TextDisabled(country.hasPort ? "| sea port" : "| no sea port");
            const Replay::Nation* nation=_pipeline ? &_pipeline->frames[_pipeline->frame_index(_pipeline->time)].nations[c]
                : _replay ? &_replay->days[size_t(replay_time())].nations[c] : nullptr;
            if (nation) {
                ImGui::Text("Population %d | jobless %.0f%% | approval %.0f%%",nation->residents,
                    100.f*nation->unemployed/std::max(1,nation->residents),100.f*nation->approval);
                if (nation->wars) ImGui::Text("Inequality (Gini) %.2f | at war with %d",nation->gini,nation->wars);
                else ImGui::Text("Inequality (Gini) %.2f | at peace",nation->gini);
            } else ImGui::TextDisabled("DEMO globe: load a recording for this country's statistics.");
            if (_pipeline) {
                const auto& frame=_pipeline->frames[_pipeline->frame_index(_pipeline->time)];
                if (_countryInfoRevision!=frame.revision) { _countryInfo=country_agents(frame.snapshot,country.id); _countryInfoRevision=frame.revision; }
                const auto& info=_countryInfo;
                ImGui::Text("Day %d: %d agents, %d hungry (%.0f%%)",frame.tick,info.people,info.hungry,100.f*info.hungry/std::max(1,info.people));
                ImGui::Text("Average health %.0f/100 | wellbeing %.0f/100 | cash %.2f WLD",info.health,info.wellbeing,
                    info.cashMinor/100.0/std::max(1,info.people));
                std::string jobs;
                for (size_t i=0;i<std::min<size_t>(4,info.roles.size());++i)
                    jobs+=fmt::format("{}{} {}",i ? ", " : "Most common: ",info.roles[i].first,info.roles[i].second);
                if (!jobs.empty()) ImGui::TextUnformatted(jobs.c_str());
            }
            if (ImGui::Button("Inspect market square",ImVec2(-1,0)) && _transition<0) {
                _yaw=std::atan2(country.center.x,country.center.z); _pitch=std::asin(country.center.y);
                _townCountry=c; _transition=0;
            }
            ImGui::End();
            if (!open) _selectedCountry=-1;
        }
        glm::vec3 eye=_target+_distance*glm::vec3(std::cos(_pitch)*std::sin(_yaw),std::sin(_pitch),std::cos(_pitch)*std::cos(_yaw));
        glm::mat4 projection=glm::perspective(glm::radians(45.f),float(_swapchainExtent.width)/_swapchainExtent.height,100.f,.05f);
        projection[1][1]*=-1;
        glm::mat4 viewproj=projection*glm::lookAt(eye,_target,glm::vec3(0,1,0));
        if (_eventReplay) _eventReplay->draw(_world,_replayRoutes,viewproj,eye,_showPanels,_yaw,_pitch);
        if (_pipeline) _pipeline->events.draw(_world,_replayRoutes,viewproj,eye,false,_yaw,_pitch,false);
        int windowWidth,windowHeight; SDL_GetWindowSize(_window,&windowWidth,&windowHeight);
        for (const auto& country : _world.countries) {
            if (glm::dot(country.center,eye-country.center)<=0) continue;
            glm::vec4 clip=viewproj*glm::vec4(country.center*1.02f,1);
            if (clip.w<=0) continue;
            glm::vec2 ndc(clip.x/clip.w,clip.y/clip.w);
            if (std::abs(ndc.x)>1 || std::abs(ndc.y)>1) continue;
            ImVec2 screen((ndc.x*.5f+.5f)*windowWidth+7,(ndc.y*.5f+.5f)*windowHeight);
            const char* name=_pipeline ? _pipeline->names[&country-_world.countries.data()].c_str() : country.name;
            ImGui::GetForegroundDrawList()->AddText(ImVec2(screen.x+1,screen.y+1),IM_COL32(0,0,0,255),name);
            ImGui::GetForegroundDrawList()->AddText(screen,IM_COL32(245,245,245,255),name);
        }
    }
    ImGui::Render();
}
void VulkanEngine::set_mouse_look(bool enabled) {
    if (_mouseLook == enabled) return;
    if (SDL_SetRelativeMouseMode(enabled ? SDL_TRUE : SDL_FALSE) != 0) {
        fmt::print("Cannot change mouse capture: {}\n", SDL_GetError());
        return;
    }
    _mouseLook = enabled;
}
void VulkanEngine::run(int frameLimit, const std::optional<std::filesystem::path>& capture) {
    bool quit=false;
    bool resizedDown=false, resizedBack=false;
    uint64_t lastTime=SDL_GetPerformanceCounter();
    const uint64_t started=lastTime;
    while (!quit && (!frameLimit || _frameNumber < uint64_t(frameLimit))) {
        uint64_t now=SDL_GetPerformanceCounter();
        float elapsed=_smoke ? .1f : std::min(.1f,float(double(now-lastTime)/SDL_GetPerformanceFrequency()));
        lastTime=now;
        if (_streamPath && (_streamPoll+=elapsed)>=1.f) {
            _streamPoll=0;
            try { load_stream(*_streamPath); }
            catch (const std::exception& error) { _pipeline->connection=std::string("Keeping last valid state: ")+error.what(); }
        }
        if (_pipeline && _scene==Scene::Globe) _pipeline->advance(elapsed);
        if (_eventReplay && _scene==Scene::Globe) _eventReplay->advance(elapsed);
        if (_playing && _scene==Scene::Ground) _town.update(elapsed);
        if (_transition>=0) {
            // Inspect dives at the country while the exposure flashes white; back fades out of the square. Swap at .9 s.
            float before=_transition; _transition+=elapsed;
            if (_scene==Scene::Globe && _transition<.9f) _distance=glm::mix(_distance,1.07f,1-std::exp(-elapsed*5));
            if (before<.9f && _transition>=.9f) {
                if (_scene==Scene::Ground) { _scene=Scene::Globe; _distance=2.2f; } else enter_ground();
            }
            if (_transition>=1.5f) _transition=-1;
        }
        if (_playing && _scene == Scene::Ocean) _oceanSeconds+=elapsed;
        else if (_playing) _demoSeconds+=elapsed*_trafficSpeed;
        if (_smoke && _frameNumber >= 4 && !resizedDown) {
            SDL_SetWindowSize(_window,960,640); _resize=true; resizedDown=true; _shadows=false; _fxaa=false;
        }
        if (_smoke && _frameNumber >= 8 && !resizedBack) {
            SDL_SetWindowSize(_window,1280,800); _resize=true; resizedBack=true; _shadows=true; _fxaa=true;
        }
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            ImGui_ImplSDL2_ProcessEvent(&event);
            if (event.type == SDL_QUIT) quit=true;
            if (event.type == SDL_KEYDOWN && !event.key.repeat) {
                if (event.key.keysym.sym == SDLK_ESCAPE) {
                    if (_mouseLook) set_mouse_look(false);
                    else quit=true;
                }
                if (!ImGui::GetIO().WantTextInput) {
                    if (event.key.keysym.sym == SDLK_h) _showPanels=!_showPanels;
                    if (event.key.keysym.sym == SDLK_r && _scene == Scene::Ocean) _headAngles = {};
                    if (event.key.keysym.sym == SDLK_r && _environment) { _cameraPosition=_cameraStart; _cameraAngles=_cameraStartAngles; }
                }
            }
            if (event.type == SDL_WINDOWEVENT && (event.window.event == SDL_WINDOWEVENT_SIZE_CHANGED || event.window.event == SDL_WINDOWEVENT_RESTORED)) _resize=true;
            if ((event.type == SDL_MOUSEBUTTONUP && event.button.button == SDL_BUTTON_RIGHT) ||
                (event.type == SDL_WINDOWEVENT && event.window.event == SDL_WINDOWEVENT_FOCUS_LOST)) set_mouse_look(false);
            if (_environment) {
                if (event.type==SDL_MOUSEBUTTONDOWN && event.button.button==SDL_BUTTON_RIGHT && !ImGui::GetIO().WantCaptureMouse) set_mouse_look(true);
                if (event.type==SDL_MOUSEMOTION && _mouseLook) {
                    _cameraAngles.x+=event.motion.xrel*.003f;
                    _cameraAngles.y=std::clamp(_cameraAngles.y-event.motion.yrel*.003f,-1.5f,1.5f);
                }
            } else if (_scene == Scene::Ocean) {
                if (event.type == SDL_MOUSEBUTTONDOWN && event.button.button == SDL_BUTTON_RIGHT && !ImGui::GetIO().WantCaptureMouse)
                    set_mouse_look(true);
                if (event.type == SDL_MOUSEMOTION && _mouseLook)
                    turn_head(_headAngles, {event.motion.xrel, event.motion.yrel});
            } else if (_scene == Scene::Ground) {
                if (!ImGui::GetIO().WantCaptureMouse && event.type == SDL_MOUSEMOTION && (event.motion.state & SDL_BUTTON_LMASK))
                    _townYaw-=event.motion.xrel*.005f;
                if (!ImGui::GetIO().WantCaptureMouse && event.type == SDL_MOUSEWHEEL)
                    _townDistance=std::clamp(_townDistance*std::exp(-event.wheel.y*.1f),8.f,45.f);
            } else if (!ImGui::GetIO().WantCaptureMouse) {
                if (event.type == SDL_MOUSEMOTION && (event.motion.state & SDL_BUTTON_LMASK)) {
                    _yaw-=event.motion.xrel*.005f; _pitch=std::clamp(_pitch+event.motion.yrel*.005f,-1.45f,1.45f);
                }
                if (event.type == SDL_MOUSEWHEEL) _distance=std::clamp(_distance*std::exp(-event.wheel.y*.1f),1.3f,20.f);
                // A click (not a drag) on the globe selects the country under the cursor; sea or space clears it.
                if (event.type == SDL_MOUSEBUTTONDOWN && event.button.button == SDL_BUTTON_LEFT) _clickStart={event.button.x,event.button.y};
                if (event.type == SDL_MOUSEBUTTONUP && event.button.button == SDL_BUTTON_LEFT && _globe && _scene == Scene::Globe
                    && glm::length(glm::vec2(event.button.x,event.button.y)-_clickStart)<5) {
                    int windowWidth,windowHeight; SDL_GetWindowSize(_window,&windowWidth,&windowHeight);
                    glm::vec2 ndc(event.button.x/float(windowWidth)*2-1,event.button.y/float(windowHeight)*2-1);
                    glm::mat4 unproject=glm::inverse(_sceneData.viewproj);
                    glm::vec4 nearPoint=unproject*glm::vec4(ndc,1,1), farPoint=unproject*glm::vec4(ndc,.5f,1);
                    glm::vec3 origin=glm::vec3(nearPoint)/nearPoint.w, ray=glm::normalize(glm::vec3(farPoint)/farPoint.w-origin);
                    float b=glm::dot(origin,ray), discriminant=b*b-glm::dot(origin,origin)+1;
                    _selectedCountry=-1; _countryInfoRevision=-1;
                    if (discriminant>=0) {
                        glm::vec3 hit=glm::normalize(origin+ray*(-b-std::sqrt(discriminant)));
                        if (world_height(hit)>.035f) _selectedCountry=world_country(_world,hit);
                    }
                }
            }
        }
        int width,height; SDL_Vulkan_GetDrawableSize(_window,&width,&height);
        if (_environment && _mouseLook && !ImGui::GetIO().WantTextInput) {
            const auto* keys=SDL_GetKeyboardState(nullptr);
            glm::vec3 forward=view_forward(_cameraAngles), right=glm::normalize(glm::cross(forward,glm::vec3(0,1,0)));
            glm::vec3 movement=forward*float(keys[SDL_SCANCODE_W]-keys[SDL_SCANCODE_S])+right*float(keys[SDL_SCANCODE_D]-keys[SDL_SCANCODE_A])+
                glm::vec3(0,float(keys[SDL_SCANCODE_E]-keys[SDL_SCANCODE_Q]),0);
            if (glm::dot(movement,movement)>0) _cameraPosition+=glm::normalize(movement)*_cameraSpeed*elapsed*(keys[SDL_SCANCODE_LSHIFT] ? 4.f:1.f);
        }
        if (width==0 || height==0 || (SDL_GetWindowFlags(_window)&SDL_WINDOW_MINIMIZED)) { SDL_Delay(20); continue; }
        if (_resize) resize_swapchain();
        // Switching to the plane or ship view loads its model on first use (a one-off pause of about a second).
        if (_scene == Scene::Ocean && _oceanView != OceanView::Meadow) load_vehicle(_oceanView == OceanView::Ship);
        frame_ui(); draw();
    }
    set_mouse_look(false);
    VK_CHECK(vkDeviceWaitIdle(_device));
    if (capture && _frameNumber) capture_frame(*capture);
    double seconds=double(SDL_GetPerformanceCounter()-started)/SDL_GetPerformanceFrequency();
    fmt::print("Rendered {} frames in {:.2f} s ({:.1f} average FPS, including capture/resizes)\n",_frameNumber,seconds,_frameNumber/std::max(seconds,.0001));
}
void VulkanEngine::capture_frame(const std::filesystem::path& path) {
    size_t bytes=size_t(_swapchainExtent.width)*_swapchainExtent.height*4;
    auto buffer=create_buffer(bytes,VK_BUFFER_USAGE_TRANSFER_DST_BIT,VMA_MEMORY_USAGE_GPU_TO_CPU);
    immediate_submit([&](VkCommandBuffer cmd) {
        VkBufferImageCopy copy{}; copy.imageSubresource.aspectMask=VK_IMAGE_ASPECT_COLOR_BIT; copy.imageSubresource.layerCount=1;
        copy.imageExtent={_swapchainExtent.width,_swapchainExtent.height,1};
        vkCmdCopyImageToBuffer(cmd,(_fxaa ? _finalImage : _tonemapImage).image,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,buffer.buffer,1,&copy);
    });
    void* data; VK_CHECK(vmaMapMemory(_allocator,buffer.allocation,&data));
    VK_CHECK(vmaInvalidateAllocation(_allocator,buffer.allocation,0,VK_WHOLE_SIZE));
    std::ofstream output(path,std::ios::binary);
    if (!output) { vmaUnmapMemory(_allocator,buffer.allocation); destroy_buffer(buffer); throw std::runtime_error("Cannot write capture"); }
    output<<"P6\n"<<_swapchainExtent.width<<' '<<_swapchainExtent.height<<"\n255\n";
    auto* pixels=static_cast<const char*>(data);
    for (size_t i=0;i<bytes;i+=4) output.write(pixels+i,3);
    bool valid=bool(output); vmaUnmapMemory(_allocator,buffer.allocation); destroy_buffer(buffer);
    check(valid,"Capture write failed"); fmt::print("Capture: {}\n",path.string());
}

void VulkanEngine::cleanup() {
    set_mouse_look(false);
    if (_device) {
        vkDeviceWaitIdle(_device);
        if (_imguiInitialized) { ImGui_ImplVulkan_Shutdown(); ImGui_ImplSDL2_Shutdown(); ImGui::DestroyContext(); _imguiInitialized=false; }
        _model.reset(); for (auto& vehicle : _vehicles) vehicle.model.reset();
        for (auto* mesh : {&_sphere,&_ground,&_planeMesh,&_shipMesh,&_portMesh,&_townMesh}) { destroy_buffer(mesh->indexBuffer); destroy_buffer(mesh->vertexBuffer); }
        for (auto& arc : _routeArcs) { destroy_buffer(arc.mesh.indexBuffer); destroy_buffer(arc.mesh.vertexBuffer); }
        destroy_buffer(_defaultMaterialBuffer);
        destroy_buffer(_globeMaterialBuffer);
        _descriptors.destroy_pools(_device);
        for (auto& frame : _frames) {
            destroy_buffer(frame.sceneBuffer);
            if (frame._commandPool) vkDestroyCommandPool(_device,frame._commandPool,nullptr);
            if (frame._renderFence) vkDestroyFence(_device,frame._renderFence,nullptr);
            if (frame._swapchainSemaphore) vkDestroySemaphore(_device,frame._swapchainSemaphore,nullptr);
        }
        metalRoughMaterial.clear_resources(_device);
        for (auto* pipeline : {&_shadowPipeline,&_tonemapPipeline,&_fxaaPipeline,&_spacePipeline,&_skyPipeline,&_waterPipeline,&_terrainPipeline,&_grassPipeline}) {
            vkDestroyPipeline(_device,pipeline->pipeline,nullptr); vkDestroyPipelineLayout(_device,pipeline->layout,nullptr);
        }
        vkDestroyDescriptorSetLayout(_device,_gpuSceneDataDescriptorLayout,nullptr); vkDestroyDescriptorSetLayout(_device,_postLayout,nullptr);
        for (auto sampler : {_shadowSampler,_defaultSamplerLinear,_postSampler,_bloomSampler}) vkDestroySampler(_device,sampler,nullptr);
        for (auto* image : {&_whiteImage,&_blackImage,&_greyImage,&_flatNormalImage,&_errorCheckerboardImage,&_shadowImage,&_earthTexture,&_earthNormal,&_earthLights,&_cloudTexture}) destroy_image(*image);
        destroy_targets(); destroy_swapchain(); _mainDeletionQueue.flush();
        vkDestroyDevice(_device,nullptr); _device={};
    }
    if (_surface) { vkDestroySurfaceKHR(_instance,_surface,nullptr); _surface={}; }
    if (_debug_messenger) { vkb::destroy_debug_utils_messenger(_instance,_debug_messenger); _debug_messenger={}; }
    if (_instance) { vkDestroyInstance(_instance,nullptr); _instance={}; }
    if (_window) { SDL_DestroyWindow(_window); _window=nullptr; }
    SDL_Quit();
}
