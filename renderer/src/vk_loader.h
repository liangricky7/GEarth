#pragma once

#include <filesystem>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

#include <vk_types.h>
#include <vk_descriptors.h>

struct GeoSurface {
    uint32_t startIndex{};
    uint32_t count{};
    Bounds bounds{};
    std::shared_ptr<struct GLTFMaterial> material;
};

struct GLTFMaterial {
    MaterialInstance data;
};

struct MeshAsset {
    std::string name;
    std::vector<GeoSurface> surfaces;
    GPUMeshBuffers meshBuffers;
};

class VulkanEngine;

struct LoadedGLTF : public IRenderable {
    std::unordered_map<std::string, std::shared_ptr<MeshAsset>> meshes;
    std::unordered_map<std::string, std::shared_ptr<Node>> nodes;
    std::unordered_map<std::string, AllocatedImage> images;
    std::unordered_map<std::string, std::shared_ptr<GLTFMaterial>> materials;
    std::vector<std::shared_ptr<Node>> topNodes;
    std::vector<VkSampler> samplers;
    std::vector<SceneLight> lights;
    std::optional<glm::mat4> camera;
    std::optional<glm::vec3> sunDirection;

    DescriptorAllocatorGrowable descriptorPool;
    AllocatedBuffer materialDataBuffer{};
    VulkanEngine* creator{};

    ~LoadedGLTF() override;

    void Draw(const glm::mat4& topMatrix, DrawContext& ctx) override;

private:
    void clearAll();
};

// bakeOcclusion: bake per-vertex sky visibility (ao_bake.h) into vertex colour alpha before upload, cached in
// "<file>.occlusion" next to the model. Used for carried models (cockpit, ship) whose geometry never moves.
std::optional<std::shared_ptr<LoadedGLTF>> loadGltf(
    VulkanEngine* engine,
    std::filesystem::path filePath,
    bool bakeOcclusion = false);
