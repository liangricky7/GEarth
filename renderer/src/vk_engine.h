#pragma once
#include "vk_types.h"
#include "vk_loader.h"
#include "vk_descriptors.h"
#include "globe.h"
#include "replay.h"
#include <future>
#include "event_replay.h"
#include "pipeline_replay.h"
#include <SDL.h>
#include <filesystem>

constexpr int FRAME_OVERLAP = 2;
struct DeletionQueue {
    std::deque<std::function<void()>> deletors;
    void push_function(std::function<void()>&& fn) { deletors.push_back(std::move(fn)); }
    void flush() { for (auto it = deletors.rbegin(); it != deletors.rend(); ++it) (*it)(); deletors.clear(); }
};
struct FrameData {
    VkCommandPool _commandPool{}; VkCommandBuffer _mainCommandBuffer{};
    VkFence _renderFence{}; VkSemaphore _swapchainSemaphore{};
    AllocatedBuffer sceneBuffer; VkDescriptorSet sceneDescriptor{};
};
struct GLTFMetallic_Roughness {
    MaterialPipeline opaquePipeline, transparentPipeline, maskPipeline;
    VkDescriptorSetLayout materialLayout{};
    struct MaterialConstants {
        glm::vec4 colorFactors{1}, metal_rough_factors{0, .7f, 0, 0}, uvTransform{}, alphaMask{}, emission{}, materialFlags{};
        glm::vec4 extra[10]{};
    };
    static_assert(sizeof(MaterialConstants) == 256);
    struct MaterialResources {
        AllocatedImage colorImage; VkSampler colorSampler{};
        AllocatedImage metalRoughImage; VkSampler metalRoughSampler{};
        AllocatedImage normalImage; VkSampler normalSampler{};
        AllocatedImage emissiveImage; VkSampler emissiveSampler{};
        VkBuffer dataBuffer{}; uint32_t dataBufferOffset{};
    };
    DescriptorWriter writer;
    void build_pipelines(class VulkanEngine*);
    void clear_resources(VkDevice);
    MaterialInstance write_material(VkDevice, MaterialPass, const MaterialResources&, DescriptorAllocatorGrowable&);
    void write_material_set(VkDevice, const MaterialResources&, VkDescriptorSet);
};
struct MeshNode : Node {
    std::shared_ptr<MeshAsset> mesh;
    void Draw(const glm::mat4&, DrawContext&) override;
};

class VulkanEngine {
public:
    ~VulkanEngine();
    enum class OceanView { Plane, Ship };
    // ocean = start over open water in that view instead of on the globe.
    // planeModel / shipModel = glTF cockpit and ship carried with the plane and ship cameras.
    void init(const std::optional<std::filesystem::path>& model, bool smoke, const std::optional<std::filesystem::path>& replay = {},
        std::optional<OceanView> ocean = {}, const std::optional<std::filesystem::path>& planeModel = {},
        const std::optional<std::filesystem::path>& shipModel = {}, const std::optional<std::filesystem::path>& events = {}, bool environment = false);
    void run(int frameLimit = 0, const std::optional<std::filesystem::path>& capture = {});
    void load_events(const std::filesystem::path& path);
    void load_stream(const std::filesystem::path& path, std::optional<PipelineReplay> prepared = std::nullopt);
    void cleanup();
    static void self_test();
    AllocatedBuffer create_buffer(size_t, VkBufferUsageFlags, VmaMemoryUsage);
    void destroy_buffer(const AllocatedBuffer&);
    GPUMeshBuffers uploadMesh(std::span<uint32_t>, std::span<Vertex>);
    AllocatedImage create_image(VkExtent3D, VkFormat, VkImageUsageFlags, bool mipmapped = false, uint32_t levels = 0);
    AllocatedImage load_dds(const std::filesystem::path&, bool srgb);
    AllocatedImage create_image(void*, VkExtent3D, VkFormat, VkImageUsageFlags, bool mipmapped = false);
    void destroy_image(const AllocatedImage&);
    void immediate_submit(std::function<void(VkCommandBuffer)>&&);
    float material_anisotropy() const { return _samplerAnisotropySupported ? std::min(8.f, _maxSamplerAnisotropy) : 1.f; }
    std::string shader_path(const char* name) const;

    VkDevice _device{}; VkPhysicalDevice _chosenGPU{};
    AllocatedImage _whiteImage, _blackImage, _greyImage, _flatNormalImage, _errorCheckerboardImage;
    VkSampler _defaultSamplerLinear{};
    VkDescriptorSetLayout _gpuSceneDataDescriptorLayout{};
    GLTFMetallic_Roughness metalRoughMaterial;
    VkFormat colorFormat{VK_FORMAT_R16G16B16A16_SFLOAT}, depthFormat{VK_FORMAT_D32_SFLOAT};
private:
    friend std::optional<std::shared_ptr<LoadedGLTF>> loadGltf(VulkanEngine*, std::filesystem::path, bool);
    void init_vulkan(); void init_commands(); void init_sync_structures();
    void create_swapchain(); void destroy_swapchain(); void resize_swapchain();
    void init_descriptors(); void init_pipelines(); void init_scene(const std::optional<std::filesystem::path>&);
    void update_scene(); void draw(); void draw_shadow(VkCommandBuffer); void draw_geometry(VkCommandBuffer);
    void draw_post(VkCommandBuffer, bool fxaa); void draw_bloom(VkCommandBuffer); void capture_frame(const std::filesystem::path&);
    void init_imgui(); void frame_ui();
    void init_globe(); void update_globe(); void draw_space(VkCommandBuffer);
    void create_targets(); void destroy_targets(); void write_post_descriptors();
    SDL_Window* _window{}; VkInstance _instance{}; VkDebugUtilsMessengerEXT _debug_messenger{};
    VkSurfaceKHR _surface{}; VmaAllocator _allocator{}; VkQueue _graphicsQueue{}; uint32_t _graphicsQueueFamily{};
    VkCommandPool _immCommandPool{}; VkCommandBuffer _immCommandBuffer{}; VkFence _immFence{};
    FrameData _frames[FRAME_OVERLAP]; DeletionQueue _mainDeletionQueue;
    VkSwapchainKHR _swapchain{}; VkExtent2D _swapchainExtent{};
    VkFormat _swapchainImageFormat{VK_FORMAT_B8G8R8A8_UNORM};
    std::vector<VkImage> _swapchainImages; std::vector<VkImageView> _swapchainImageViews;
    std::vector<VkSemaphore> _swapchainRenderSemaphores;
    AllocatedImage _drawImage, _depthImage, _tonemapImage, _finalImage, _shadowImage, _bloomImage;
    VkSampler _shadowSampler{}, _postSampler{}, _bloomSampler{}; DescriptorAllocatorGrowable _descriptors;
    VkDescriptorSetLayout _postLayout{}; VkDescriptorSet _tonemapSet{}, _fxaaSet{};
    MaterialPipeline _shadowPipeline, _tonemapPipeline, _fxaaPipeline, _spacePipeline, _skyPipeline, _waterPipeline;
    void update_ocean(); void draw_ocean(VkCommandBuffer cmd);
    std::shared_ptr<LoadedGLTF> _model;
    GPUMeshBuffers _sphere, _ground; uint32_t _sphereIndices{}, _groundIndices{};
    AllocatedBuffer _defaultMaterialBuffer; MaterialInstance _sphereMaterial, _groundMaterial;
    AllocatedImage _earthTexture, _earthNormal, _earthLights, _cloudTexture;
    AllocatedBuffer _globeMaterialBuffer;
    MaterialInstance _cloudMaterial, _atmosphereMaterial, _planeMaterial, _shipMaterial, _portMaterial;
    // Replay overlays: route arcs idle / carrying cargo / closed, and cargo trails per good.
    MaterialInstance _routeMaterials[3], _cargoMaterials[4];
    struct RouteArc { GPUMeshBuffers mesh; uint32_t indexCount{}; int forward{}, backward{-1}; };
    std::vector<RouteArc> _routeArcs;
    GPUMeshBuffers _planeMesh, _shipMesh, _portMesh; uint32_t _planeIndices{}, _shipIndices{};
    std::vector<DemoRoute> _routes;
    // Recorded traffic replaces the demo; _replayRoutes follows the active replay's route indices.
    std::optional<std::filesystem::path> _replayPath;
    std::optional<Replay> _replay;
    std::optional<EventReplay> _eventReplay;
    std::optional<PipelineReplay> _pipeline;
    std::optional<std::filesystem::path> _streamPath;
    std::filesystem::file_time_type _streamModified{};
    float _streamPoll{};
    std::vector<DemoRoute> _replayRoutes;
    float replay_time() const { return _replay->is_snapshot() ? 0.f : std::fmod(_demoSeconds,float(_replay->days.size())); }
    ProceduralWorld _world;
    bool _globe{}, _clouds{}, _spaceBackground{true}, _atmosphere{}, _traffic{true}, _playing{true};
    float _demoSeconds{}, _trafficSpeed{1.f}, _cityLightIntensity{9.f}, _moonIntensity{.45f};
    bool _night{};
    bool _vehicleShadows{true}; // ocean views: sun shadows inside the cockpit/ship, separate from the globe setting
    bool _showPanels{true}; // H toggles the control panels (HUD, captions and labels stay) for clean recordings
    enum class Scene { Globe, Ocean };
    Scene _scene{Scene::Globe};
    float _oceanSeconds{};
    OceanPushConstants _oceanPush;
    OceanView _oceanView{OceanView::Plane};
    glm::vec2 _headAngles{};
    bool _mouseLook{};
    void set_mouse_look(bool enabled);
    // Models carried in the plane [0] and ship [1] vehicle frames (glTF +Z forward, +Y up): offset in metres,
    // uniform scale, yaw in degrees. Defaults place the eye in the captain's seat of models/boeing_737_cockpit (pilot
    // seats span z .82-1.27 with the cushion at y 1.72; +X appears on screen-left). For models/free_plane_cockpit use
    // offset (-1.041, -1.422, 1.174), scale .0254, yaw 0 (modelled in inches). The ship default stands
    // at the bow of models/cargo_ship (bow at -Z, scaled 5x to a ~120 m freighter).
    struct Vehicle {
        std::shared_ptr<LoadedGLTF> model;
        glm::vec3 offset{};
        float scale{1}, yaw{0};
    };
    Vehicle _vehicles[2]{{{}, {-.25f, -2.20f, -1.25f}, 1, 0}, {{}, {0, -17.75f, -59.25f}, 5, 180}};
    // Vehicle models load on first use (the start view's at startup), so a globe start skips them entirely.
    std::optional<std::filesystem::path> _vehiclePaths[2];
    void load_vehicle(int index);
    // The planet map is generated on a worker thread while Vulkan starts up.
    std::future<ProceduralWorld> _worldFuture;
    Vehicle* active_vehicle() { return &_vehicles[_oceanView == OceanView::Ship]; }
    // Plane and ship captions name a real replay shipment on an air or sea route when one exists.
    std::string _planeCaption{"GEarth cargo view. Camera and ocean motion are illustrative."};
    std::string _shipCaption{"GEarth cargo view. Camera and ocean motion are illustrative."};
    glm::vec4 _sunGlare{}; // tonemap: sun uv xy, strength, aspect
    DrawContext _drawContext; glm::mat4 _modelTransform{1}; GPUSceneData _sceneData;
    glm::vec3 _target{}, _sunDirection{.5f, 1.f, .4f};
    float _yaw{.65f}, _pitch{.3f}, _distance{4.5f}, _exposure{1.f}, _bloom{.06f}, _sunIntensity{3.f}, _ambient{.2f};
    bool _shadows{true}, _fxaa{true}, _showGround{true}, _smoke{}, _imguiInitialized{}, _resize{};
    bool _samplerAnisotropySupported{}; float _maxSamplerAnisotropy{1};
    bool _textureCompressionBC{}; size_t _ddsBytes{};
    bool _environment{};
    glm::vec3 _cameraPosition{}, _cameraStart{};
    glm::vec2 _cameraAngles{}, _cameraStartAngles{};
    float _cameraSpeed{5}, _lightExposure{.01f};
    size_t _visibleSurfaces{};
    uint64_t _frameNumber{};
};
