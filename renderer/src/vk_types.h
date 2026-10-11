#pragma once
#include <array>
#include <cstddef>
#include <deque>
#include <functional>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <vector>
#include <vulkan/vulkan.h>
#include <vulkan/vk_enum_string_helper.h>
#include <vk_mem_alloc.h>
#include <fmt/core.h>
#include <glm/glm.hpp>
#include <stdexcept>

#define VK_CHECK(x) do { const VkResult vkCheckResult = (x); if (vkCheckResult != VK_SUCCESS) \
    throw std::runtime_error(std::string("Vulkan: ") + string_VkResult(vkCheckResult)); } while (0)

struct AllocatedImage {
    VkImage image{}; VkImageView imageView{}; VmaAllocation allocation{};
    VkExtent3D imageExtent{}; VkFormat imageFormat{};
};
struct AllocatedBuffer {
    VkBuffer buffer{}; VmaAllocation allocation{}; VmaAllocationInfo info{};
};
// Extracted vertex ABI: shaders/vertex.glsl must match byte for byte.
struct Vertex {
    glm::vec3 position{}; float uv_x{};
    glm::vec3 normal{}; float uv_y{};
    glm::vec4 color{1.0f}; glm::vec4 tangent{};
};
static_assert(sizeof(Vertex) == 64 && offsetof(Vertex, tangent) == 48);
struct GPUMeshBuffers {
    AllocatedBuffer indexBuffer, vertexBuffer;
    VkDeviceAddress vertexBufferAddress{};
};
struct GPUDrawPushConstants { glm::mat4 worldMatrix{1}; VkDeviceAddress vertexBuffer{}; };
struct SceneLight { glm::vec4 positionRange{}, colorIntensity{}, directionOuter{}, innerType{}; };
struct GPUSceneData {
    glm::mat4 viewproj{1}, sunViewProjection{1};
    glm::vec4 cameraPosition{}, sunlightDirection{}, sunlightColor{}, ambientColor{}, shadowSettings{};
    // Globe: xyz = capital direction, w = that country's city-light brightness.
    std::array<glm::vec4,10> cityLights{};
    // x = city-light intensity, y = sun visibility (0 = sun off), z = replay days or demo seconds, w unused.
    glm::vec4 nightSettings{};
    // Night-view moonlight: xyz = direction toward it, w = intensity (0 = off).
    glm::vec4 moonLight{};
    glm::vec4 lightInfo{};
    std::array<SceneLight,128> lights{};
};
static_assert(sizeof(GPUSceneData) == 8608);
struct StarfieldPushConstants {
    glm::mat4 rotationProjection{1};
    glm::vec4 viewport{};
};
static_assert(sizeof(StarfieldPushConstants) == 80);
// Ocean flight scene (shaders/ocean.glsl): x = time, y/z = camera x/z; viewport xy = framebuffer pixels.
struct OceanPushConstants {
    glm::mat4 inverseViewProj{1};
    glm::vec4 params{}, viewport{}, wind{};
};
static_assert(sizeof(OceanPushConstants) == 112);
enum class MaterialPass : uint8_t { MainColor, Transparent, Mask, Other };
struct MaterialPipeline { VkPipeline pipeline{}; VkPipelineLayout layout{}; };
struct MaterialInstance {
    MaterialPipeline* pipeline{}; VkDescriptorSet materialSet{};
    MaterialPass passType{MaterialPass::MainColor};
};
struct Bounds { glm::vec3 origin{}; float sphereRadius{}; glm::vec3 extents{}; };
struct DrawContext;
struct IRenderable { virtual ~IRenderable() = default;
    virtual void Draw(const glm::mat4&, DrawContext&) = 0; };
struct RenderObject {
    uint32_t indexCount{}, firstIndex{}; VkBuffer indexBuffer{};
    MaterialInstance* material{}; Bounds bounds{};
    glm::mat4 transform{1}; VkDeviceAddress vertexBufferAddress{};
};
struct DrawContext { std::vector<RenderObject> OpaqueSurfaces, TransparentSurfaces; };
struct Node : IRenderable {
    std::weak_ptr<Node> parent; std::vector<std::shared_ptr<Node>> children;
    glm::mat4 localTransform{1}, worldTransform{1};
    void refreshTransform(const glm::mat4& matrix) {
        worldTransform = matrix * localTransform;
        for (auto& child : children) child->refreshTransform(worldTransform);
    }
    void Draw(const glm::mat4& matrix, DrawContext& ctx) override {
        for (auto& child : children) child->Draw(matrix, ctx);
    }
};
