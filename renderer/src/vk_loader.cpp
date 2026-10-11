#include "vk_loader.h"
#include "vk_engine.h"
#include "ao_bake.h"
#include <fastgltf/parser.hpp>
#include <fastgltf/tools.hpp>
#include <fastgltf/glm_element_traits.hpp>
#include <stb_image.h>
#include <glm/gtc/matrix_transform.hpp>
#include <glm/gtc/quaternion.hpp>
#include <glm/gtc/constants.hpp>
#include <algorithm>
#include <climits>
#include <cstring>
#include <fstream>

namespace {
void validate_renderer_asset(const fastgltf::Asset& asset)
{
    // shortcut: dense, static glTF only; add sparse/skinned support when an actual asset requires it.
    for (const auto& view : asset.bufferViews) {
        const auto& buffer = asset.buffers[view.bufferIndex];
        const size_t size = std::visit([](const auto& source) -> size_t {
            if constexpr (requires { source.bytes.size(); }) return source.bytes.size();
            return 0;
        }, buffer.data);
        if (view.byteOffset > size || view.byteLength > size - view.byteOffset)
            throw std::runtime_error("glTF buffer view exceeds loaded buffer");
    }
    for (const auto& accessor : asset.accessors) {
        if (accessor.sparse || !accessor.bufferViewIndex || accessor.count == 0)
            throw std::runtime_error("Export dense glTF accessors");
        const auto& view = asset.bufferViews[*accessor.bufferViewIndex];
        size_t element = fastgltf::getElementByteSize(accessor.type, accessor.componentType);
        size_t stride = view.byteStride.value_or(element);
        if (stride < element || accessor.byteOffset > view.byteLength || element > view.byteLength - accessor.byteOffset ||
            accessor.count - 1 > (view.byteLength - accessor.byteOffset - element) / stride)
            throw std::runtime_error("glTF accessor exceeds buffer view");
    }
    for (const auto& mesh : asset.meshes) for (const auto& primitive : mesh.primitives) {
        if (primitive.indicesAccessor && *primitive.indicesAccessor >= asset.accessors.size())
            throw std::runtime_error("Invalid glTF index accessor");
        const auto position = primitive.findAttribute("POSITION");
        if (position == primitive.attributes.end()) continue;
        for (const auto& attribute : primitive.attributes)
            if (asset.accessors[attribute.second].count != asset.accessors[position->second].count)
                throw std::runtime_error("glTF vertex attribute counts differ");
        if (primitive.indicesAccessor) {
            const auto& indices = asset.accessors[*primitive.indicesAccessor];
            if (indices.type != fastgltf::AccessorType::Scalar ||
                (indices.componentType != fastgltf::ComponentType::UnsignedByte &&
                 indices.componentType != fastgltf::ComponentType::UnsignedShort &&
                 indices.componentType != fastgltf::ComponentType::UnsignedInt))
                throw std::runtime_error("glTF indices must be unsigned scalar values");
            if (primitive.type == fastgltf::PrimitiveType::Triangles && indices.count % 3)
                throw std::runtime_error("glTF triangle indices must form triples");
        }
    }
    std::vector<uint8_t> state(asset.nodes.size());
    std::vector<int> parents(asset.nodes.size());
    std::function<void(size_t, int)> visit = [&](size_t index, int depth) {
        if (depth > 128 || state[index] == 1) throw std::runtime_error("glTF hierarchy is cyclic or too deep");
        if (state[index] == 2) return;
        state[index] = 1;
        if (asset.nodes[index].skinIndex) throw std::runtime_error("Skinned meshes are not supported");
        for (size_t child : asset.nodes[index].children) {
            if (child >= asset.nodes.size() || ++parents[child] > 1) throw std::runtime_error("Invalid glTF child node");
            visit(child, depth+1);
        }
        state[index] = 2;
    };
    for (size_t i = 0; i < asset.nodes.size(); ++i) visit(i, 0);
}

VkFilter extract_filter(fastgltf::Filter filter)
{
    switch (filter) {
    // nearest samplers
    case fastgltf::Filter::Nearest:
    case fastgltf::Filter::NearestMipMapNearest:
    case fastgltf::Filter::NearestMipMapLinear:
        return VK_FILTER_NEAREST;

    // linear samplers
    case fastgltf::Filter::Linear:
    case fastgltf::Filter::LinearMipMapNearest:
    case fastgltf::Filter::LinearMipMapLinear:
    default:
        return VK_FILTER_LINEAR;
    }
}

VkSamplerMipmapMode extract_mipmap_mode(fastgltf::Filter filter)
{
    switch (filter) {
    case fastgltf::Filter::NearestMipMapNearest:
    case fastgltf::Filter::LinearMipMapNearest:
        return VK_SAMPLER_MIPMAP_MODE_NEAREST;

    case fastgltf::Filter::NearestMipMapLinear:
    case fastgltf::Filter::LinearMipMapLinear:
    default:
        return VK_SAMPLER_MIPMAP_MODE_LINEAR;
    }
}

}

LoadedGLTF::~LoadedGLTF() { clearAll(); }

void LoadedGLTF::clearAll() {
    if (!creator) return;
    descriptorPool.destroy_pools(creator->_device);
    for (const auto& [key, mesh] : meshes) {
        creator->destroy_buffer(mesh->meshBuffers.indexBuffer);
        creator->destroy_buffer(mesh->meshBuffers.vertexBuffer);
    }
    for (const auto& [key, image] : images) creator->destroy_image(image);
    for (VkSampler sampler : samplers) vkDestroySampler(creator->_device, sampler, nullptr);
    creator->destroy_buffer(materialDataBuffer);
}

void LoadedGLTF::Draw(const glm::mat4& matrix, DrawContext& context) {
    for (const auto& root : topNodes) root->Draw(matrix, context);
}

std::optional<std::shared_ptr<LoadedGLTF>> loadGltf(
    VulkanEngine* engine, std::filesystem::path filePath, bool bakeOcclusion) {
    fastgltf::GltfDataBuffer input;
    if (!input.loadFromFile(filePath)) return {};
    fastgltf::Parser parser(fastgltf::Extensions::MSFT_texture_dds | fastgltf::Extensions::KHR_materials_pbrSpecularGlossiness |
        fastgltf::Extensions::KHR_lights_punctual | fastgltf::Extensions::KHR_materials_transmission);
    constexpr auto options = fastgltf::Options::DontRequireValidAssetMember | fastgltf::Options::AllowDouble |
        fastgltf::Options::LoadGLBBuffers | fastgltf::Options::LoadExternalBuffers;
    auto type = fastgltf::determineGltfFileType(&input);
    if (type != fastgltf::GltfType::glTF && type != fastgltf::GltfType::GLB) return {};
    auto result = type == fastgltf::GltfType::GLB
        ? parser.loadBinaryGLTF(&input, filePath.parent_path(), options)
        : parser.loadGLTF(&input, filePath.parent_path(), options);
    if (!result || fastgltf::validate(result.get()) != fastgltf::Error::None) return {};
    auto& gltf = result.get();
    validate_renderer_asset(gltf);
    auto scene = std::make_shared<LoadedGLTF>();
    scene->creator = engine;

    for (const auto& source : gltf.samplers) {
        VkSamplerCreateInfo info{.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
        info.magFilter = extract_filter(source.magFilter.value_or(fastgltf::Filter::Nearest));
        info.minFilter = extract_filter(source.minFilter.value_or(fastgltf::Filter::Nearest));
        info.mipmapMode = extract_mipmap_mode(source.minFilter.value_or(fastgltf::Filter::Nearest));
        info.addressModeU = info.addressModeV = info.addressModeW = VK_SAMPLER_ADDRESS_MODE_REPEAT;
        info.maxLod = VK_LOD_CLAMP_NONE;
        info.maxAnisotropy = engine->material_anisotropy();
        info.anisotropyEnable = info.minFilter == VK_FILTER_LINEAR && info.maxAnisotropy > 1;
        scene->samplers.push_back(VK_NULL_HANDLE);
        VK_CHECK(vkCreateSampler(engine->_device, &info, nullptr, &scene->samplers.back()));
    }

    auto bytes = [](const auto& storage) -> std::span<const stbi_uc> {
        return std::visit([](const auto& source) -> std::span<const stbi_uc> {
            if constexpr (requires { source.bytes.data(); source.bytes.size(); })
                return {reinterpret_cast<const stbi_uc*>(source.bytes.data()), source.bytes.size()};
            return {};
        }, storage);
    };
    std::vector<std::array<AllocatedImage, 2>> textures(gltf.images.size());
    auto imageFor = [&](size_t index, bool srgb) -> AllocatedImage {
        auto& cached = textures.at(index)[srgb];
        if (cached.image) return cached;
        int width = 0, height = 0, channels = 0;
        auto& source = gltf.images[index];
        stbi_uc* decoded = nullptr;
        if (auto* uri = std::get_if<fastgltf::sources::URI>(&source.data)) {
            if (uri->fileByteOffset == 0 && uri->uri.isLocalPath()) {
                auto path = filePath.parent_path() / std::string(uri->uri.path());
                if (path.extension() == ".dds") {
                    cached = engine->load_dds(path, srgb);
                    scene->images[std::to_string(index * 2 + srgb)] = cached;
                    return cached;
                }
                decoded = stbi_load(path.string().c_str(), &width, &height, &channels, 4);
            }
        } else {
            auto encoded = bytes(source.data);
            if (auto* ref = std::get_if<fastgltf::sources::BufferView>(&source.data)) {
                const auto& view = gltf.bufferViews.at(ref->bufferViewIndex);
                auto buffer = bytes(gltf.buffers.at(view.bufferIndex).data);
                if (view.byteOffset <= buffer.size() && view.byteLength <= buffer.size() - view.byteOffset)
                    encoded = buffer.subspan(view.byteOffset, view.byteLength);
            }
            if (!encoded.empty() && encoded.size() <= INT_MAX)
                decoded = stbi_load_from_memory(encoded.data(), int(encoded.size()), &width, &height, &channels, 4);
        }
        std::unique_ptr<stbi_uc, decltype(&stbi_image_free)> pixels(decoded, stbi_image_free);
        if (!pixels || width <= 0 || height <= 0) {
            fmt::print("Failed to load GLTF image {} in {}\n", index, filePath.string());
            return cached = engine->_errorCheckerboardImage;
        }
        cached = engine->create_image(pixels.get(), {uint32_t(width), uint32_t(height), 1},
            srgb ? VK_FORMAT_R8G8B8A8_SRGB : VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT, true);
        // Only owned images enter this map; fallback images belong to the engine.
        scene->images[std::to_string(index * 2 + srgb)] = cached;
        return cached;
    };
    auto bindTexture = [&](const auto& reference, bool srgb, AllocatedImage& image, VkSampler& sampler) {
        if (!reference) return;
        const auto& texture = gltf.textures.at(reference->textureIndex);
        if (texture.ddsImageIndex) image = imageFor(*texture.ddsImageIndex, srgb);
        else if (texture.imageIndex) image = imageFor(*texture.imageIndex, srgb);
        if (texture.samplerIndex) sampler = scene->samplers.at(*texture.samplerIndex);
    };

    using Material = GLTFMetallic_Roughness;
    const size_t materialCount = gltf.materials.size() + 1;
    std::array ratios{DescriptorAllocatorGrowable::PoolSizeRatio{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 1},
        DescriptorAllocatorGrowable::PoolSizeRatio{VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 4}};
    scene->descriptorPool.init(engine->_device, uint32_t(materialCount), ratios);
    scene->materialDataBuffer = engine->create_buffer(materialCount * sizeof(Material::MaterialConstants),
        VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT, VMA_MEMORY_USAGE_CPU_TO_GPU);
    auto* constants = static_cast<Material::MaterialConstants*>(scene->materialDataBuffer.info.pMappedData);
    std::vector<std::shared_ptr<GLTFMaterial>> materials(materialCount);
    for (size_t i = 0; i < materialCount; ++i) {
        constants[i] = {};
        auto& c = constants[i];
        c.metal_rough_factors = {1, 1, 0, 0};
        MaterialPass pass = MaterialPass::MainColor;
        Material::MaterialResources r;
        r.colorImage = r.metalRoughImage = engine->_whiteImage;
        r.normalImage = engine->_flatNormalImage;
        r.colorSampler = r.metalRoughSampler = r.normalSampler = engine->_defaultSamplerLinear;
        r.dataBuffer = scene->materialDataBuffer.buffer;
        r.dataBufferOffset = uint32_t(i * sizeof(*constants));
        if (i < gltf.materials.size()) {
            const auto& m = gltf.materials[i];
            for (int k = 0; k < 4; ++k) c.colorFactors[k] = m.pbrData.baseColorFactor[k];
            c.metal_rough_factors = {m.pbrData.metallicFactor, m.pbrData.roughnessFactor, 0, 0};
            for (int k = 0; k < 3; ++k) c.emission[k] = m.emissiveFactor[k] * m.emissiveStrength.value_or(1.f);
            if (m.alphaMode == fastgltf::AlphaMode::Blend) pass = MaterialPass::Transparent;
            if (m.alphaMode == fastgltf::AlphaMode::Mask) { pass = MaterialPass::Mask; c.alphaMask.x = m.alphaCutoff; }
            c.materialFlags.x = m.normalTexture ? float(m.normalTexture->scale) : 0;
            bindTexture(m.pbrData.baseColorTexture, true, r.colorImage, r.colorSampler);
            bindTexture(m.pbrData.metallicRoughnessTexture, false, r.metalRoughImage, r.metalRoughSampler);
            bindTexture(m.normalTexture, false, r.normalImage, r.normalSampler);
            if (m.emissiveTexture) {
                c.extra[1].x = 1;
                bindTexture(m.emissiveTexture, true, r.emissiveImage, r.emissiveSampler);
            }
            if (m.specularGlossiness) {
                const auto& sg = *m.specularGlossiness;
                for (int k=0;k<4;++k) c.colorFactors[k]=sg.diffuseFactor[k];
                c.extra[0]={sg.specularFactor[0],sg.specularFactor[1],sg.specularFactor[2],sg.glossinessFactor};
                c.extra[1].y=1;
                r.colorImage=r.metalRoughImage=engine->_whiteImage;
                bindTexture(sg.diffuseTexture,true,r.colorImage,r.colorSampler);
                bindTexture(sg.specularGlossinessTexture,true,r.metalRoughImage,r.metalRoughSampler);
            }
            if (m.transmission && m.transmission->transmissionFactor > 0) {
                // shortcut: thin blended glass, replace with refracted scene sampling for accurate transmission.
                pass=MaterialPass::Transparent; c.extra[1].z=m.transmission->transmissionFactor;
            }
        }
        materials[i] = std::make_shared<GLTFMaterial>();
        materials[i]->data = engine->metalRoughMaterial.write_material(engine->_device, pass, r, scene->descriptorPool);
        scene->materials[std::to_string(i)] = materials[i];
    }
    VK_CHECK(vmaFlushAllocation(engine->_allocator, scene->materialDataBuffer.allocation, 0, VK_WHOLE_SIZE));

    struct PendingMesh { MeshAsset* mesh; std::vector<uint32_t> indices; std::vector<Vertex> vertices; };
    std::vector<PendingMesh> pending;
    std::vector<std::shared_ptr<MeshAsset>> meshes;
    size_t totalIndices = 0;
    for (const auto& source : gltf.meshes) {
        auto mesh = std::make_shared<MeshAsset>();
        mesh->name = std::string(source.name);
        scene->meshes[std::to_string(meshes.size())] = mesh;
        meshes.push_back(mesh);
        PendingMesh data{mesh.get()};
        for (const auto& primitive : source.primitives) {
            auto position = primitive.findAttribute("POSITION");
            if (position == primitive.attributes.end() || !primitive.indicesAccessor ||
                primitive.type != fastgltf::PrimitiveType::Triangles) continue;
            const auto& accessor = gltf.accessors[position->second];
            if (!accessor.count) continue;
            if (accessor.type != fastgltf::AccessorType::Vec3) throw std::runtime_error("Expected VEC3 positions");
            const size_t base = data.vertices.size();
            if (accessor.count > UINT32_MAX - base) throw std::runtime_error("Mesh exceeds 32-bit vertex indexing");
            data.vertices.resize(base + accessor.count);
            for (size_t v = base; v < data.vertices.size(); ++v) data.vertices[v].normal = {0, 0, 1};
            fastgltf::iterateAccessorWithIndex<glm::vec3>(gltf, accessor,
                [&](glm::vec3 p, size_t v) { data.vertices[base + v].position = p; });
            auto attribute = [&]<typename T>(const char* name, auto assign) {
                auto found = primitive.findAttribute(name);
                if (found == primitive.attributes.end()) return;
                fastgltf::iterateAccessorWithIndex<T>(gltf, gltf.accessors[found->second],
                    [&](T value, size_t v) { assign(data.vertices[base + v], value); });
            };
            attribute.operator()<glm::vec3>("NORMAL", [](Vertex& v, glm::vec3 n) { v.normal = n; });
            attribute.operator()<glm::vec2>("TEXCOORD_0", [](Vertex& v, glm::vec2 uv) { v.uv_x = uv.x; v.uv_y = uv.y; });
            attribute.operator()<glm::vec4>("TANGENT", [](Vertex& v, glm::vec4 t) { v.tangent = t; });
            auto color = primitive.findAttribute("COLOR_0");
            if (color != primitive.attributes.end() && gltf.accessors[color->second].type == fastgltf::AccessorType::Vec3)
                attribute.operator()<glm::vec3>("COLOR_0", [](Vertex& v, glm::vec3 c) { v.color = glm::vec4(c, 1); });
            else attribute.operator()<glm::vec4>("COLOR_0", [](Vertex& v, glm::vec4 c) { v.color = c; });
            GeoSurface surface;
            surface.startIndex = uint32_t(data.indices.size());
            fastgltf::iterateAccessor<uint32_t>(gltf, gltf.accessors[*primitive.indicesAccessor], [&](uint32_t index) {
                if (index >= accessor.count) throw std::runtime_error("glTF index exceeds vertex count");
                data.indices.push_back(uint32_t(base) + index);
            });
            surface.count = uint32_t(data.indices.size()) - surface.startIndex;
            surface.material = materials.at(primitive.materialIndex.value_or(gltf.materials.size()));
            glm::vec3 lo = data.vertices[base].position, hi = lo;
            for (size_t v = base; v < data.vertices.size(); ++v) {
                lo = glm::min(lo, data.vertices[v].position); hi = glm::max(hi, data.vertices[v].position);
            }
            surface.bounds.origin = (lo + hi) * .5f;
            surface.bounds.extents = (hi - lo) * .5f;
            surface.bounds.sphereRadius = glm::length(surface.bounds.extents);
            mesh->surfaces.push_back(surface);
        }
        totalIndices += data.indices.size();
        if (data.indices.empty()) continue;
        if (bakeOcclusion) pending.push_back(std::move(data));
        else mesh->meshBuffers = engine->uploadMesh(data.indices, data.vertices);
    }
    if (!totalIndices) return {};

    std::vector<std::shared_ptr<Node>> nodes;
    for (const auto& source : gltf.nodes) {
        std::shared_ptr<Node> node;
        if (source.meshIndex) {
            auto drawable = std::make_shared<MeshNode>();
            drawable->mesh = meshes.at(*source.meshIndex);
            node = drawable;
        } else node = std::make_shared<Node>();
        std::visit(fastgltf::visitor{
            [&](const fastgltf::Node::TransformMatrix& m) { std::memcpy(&node->localTransform, m.data(), sizeof(glm::mat4)); },
            [&](const fastgltf::Node::TRS& t) {
                node->localTransform = glm::translate(glm::mat4(1), glm::vec3(t.translation[0], t.translation[1], t.translation[2])) *
                    glm::mat4_cast(glm::quat(t.rotation[3], t.rotation[0], t.rotation[1], t.rotation[2])) *
                    glm::scale(glm::mat4(1), glm::vec3(t.scale[0], t.scale[1], t.scale[2]));
            }}, source.transform);
        scene->nodes[std::to_string(nodes.size())] = node;
        nodes.push_back(node);
    }
    for (size_t i = 0; i < nodes.size(); ++i) for (size_t child : gltf.nodes[i].children) {
        nodes[i]->children.push_back(nodes.at(child));
        nodes[child]->parent = nodes[i];
    }
    for (auto& node : nodes) if (node->parent.expired()) {
        scene->topNodes.push_back(node);
        node->refreshTransform(glm::mat4(1));
    }
    fmt::print("Loaded {} meshes, {} indices from {}\n", meshes.size(), totalIndices, filePath.string());
    for (size_t i=0;i<nodes.size();++i) {
        const auto& source=gltf.nodes[i]; const auto& transform=nodes[i]->worldTransform;
        if (source.cameraIndex && !scene->camera) scene->camera=transform;
        if (!source.lightIndex) continue;
        const auto& light=gltf.lights.at(*source.lightIndex);
        glm::vec3 direction=glm::normalize(glm::vec3(transform*glm::vec4(0,0,-1,0)));
        if (light.type==fastgltf::LightType::Directional) { scene->sunDirection=-direction; continue; }
        if (scene->lights.size()==128) throw std::runtime_error("Scene exceeds 128 local lights");
        SceneLight gpu;
        gpu.positionRange=glm::vec4(glm::vec3(transform[3]),light.range.value_or(0));
        gpu.colorIntensity={light.color[0],light.color[1],light.color[2],light.intensity};
        gpu.directionOuter=glm::vec4(direction,std::cos(light.outerConeAngle.value_or(glm::quarter_pi<float>())));
        gpu.innerType={std::cos(light.innerConeAngle.value_or(0)),light.type==fastgltf::LightType::Spot ? 1.f : 0.f,0,0};
        scene->lights.push_back(gpu);
    }
    fmt::print("Scene: {} local lights; authored camera {}; DDS GPU memory {:.1f} MiB\n",scene->lights.size(),bool(scene->camera),engine->_ddsBytes/1048576.);
    if (bakeOcclusion) {
        size_t vertexCount = 0;
        for (const auto& item : pending) vertexCount += item.vertices.size();
        // Cache keyed by vertex count, model file size and ray count (not file time, so copied models keep their cache).
        constexpr int Rays = 40; // also part of the cache key, so changing it re-bakes
        uint64_t key[3] = {vertexCount, uint64_t(std::filesystem::file_size(filePath)), Rays * 10 + 2}; // x10 + smoothing passes
        auto cachePath = std::filesystem::path(filePath).concat(".occlusion");
        std::vector<uint8_t> occlusion(vertexCount);
        bool cached = false;
        if (std::ifstream cache{cachePath, std::ios::binary}) {
            uint64_t stored[3]{};
            cache.read(reinterpret_cast<char*>(stored), sizeof(stored));
            cached = std::equal(std::begin(key), std::end(key), std::begin(stored))
                && cache.read(reinterpret_cast<char*>(occlusion.data()), std::streamsize(vertexCount));
        }
        if (!cached) {
            // World transform of each mesh's first instance; every instance contributes occluding triangles.
            std::unordered_map<const MeshAsset*, glm::mat4> placement;
            std::vector<glm::vec3> triangles;
            glm::vec3 lo{1e30f}, hi{-1e30f};
            std::unordered_map<const MeshAsset*, const PendingMesh*> byMesh;
            for (const auto& item : pending) byMesh[item.mesh] = &item;
            for (const auto& [name, node] : scene->nodes) {
                auto meshNode = std::dynamic_pointer_cast<MeshNode>(node);
                if (!meshNode || !byMesh.count(meshNode->mesh.get())) continue;
                const auto& item = *byMesh[meshNode->mesh.get()];
                placement.try_emplace(item.mesh, node->worldTransform);
                for (uint32_t index : item.indices) {
                    glm::vec3 p(node->worldTransform * glm::vec4(item.vertices[index].position, 1));
                    triangles.push_back(p); lo = glm::min(lo, p); hi = glm::max(hi, p);
                }
            }
            std::vector<glm::vec3> points, normals;
            points.reserve(vertexCount); normals.reserve(vertexCount);
            for (const auto& item : pending) {
                glm::mat4 world = placement.count(item.mesh) ? placement[item.mesh] : glm::mat4(1);
                glm::mat3 normalMatrix = glm::transpose(glm::inverse(glm::mat3(world)));
                for (const auto& v : item.vertices) { points.push_back(glm::vec3(world * glm::vec4(v.position, 1))); normals.push_back(normalMatrix * v.normal); }
            }
            // Rays reach about a third of the model: far enough that the cabin interior is shaded by its walls and
            // only surfaces facing the windows stay bright.
            float reach = glm::length(hi - lo) * .3f;
            std::vector<float> visibility(vertexCount);
            auto start = std::chrono::steady_clock::now();
            bake_sky_visibility(triangles, points, normals, reach, Rays, visibility);
            // Two smoothing passes over each mesh's triangle connectivity: per-vertex ray noise otherwise shows as
            // blotches across large triangles (a ceiling is a few big faces).
            size_t base = 0;
            for (const auto& item : pending) {
                std::vector<float> sum(item.vertices.size()), weight(item.vertices.size());
                for (int pass = 0; pass < 2; ++pass) {
                    std::fill(sum.begin(), sum.end(), 0.f); std::fill(weight.begin(), weight.end(), 0.f);
                    for (size_t t = 0; t + 2 < item.indices.size(); t += 3)
                        for (int corner = 0; corner < 3; ++corner) {
                            uint32_t self = item.indices[t+corner];
                            for (int other = 0; other < 3; ++other) {
                                sum[self] += visibility[base + item.indices[t+other]]; weight[self] += 1;
                            }
                        }
                    for (size_t v = 0; v < item.vertices.size(); ++v) if (weight[v] > 0) visibility[base+v] = sum[v] / weight[v];
                }
                base += item.vertices.size();
            }
            for (size_t i = 0; i < vertexCount; ++i) occlusion[i] = uint8_t(std::clamp(visibility[i], 0.f, 1.f) * 255.f + .5f);
            fmt::print("Baked sky visibility for {} vertices against {} triangles in {:.1f} s\n", vertexCount, triangles.size() / 3,
                std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count());
            if (std::ofstream cache{cachePath, std::ios::binary}) {
                cache.write(reinterpret_cast<const char*>(key), sizeof(key));
                cache.write(reinterpret_cast<const char*>(occlusion.data()), std::streamsize(vertexCount));
            }
        } else {
            fmt::print("Sky visibility loaded from {}\n", cachePath.filename().string());
        }
        size_t at = 0;
        for (auto& item : pending) {
            for (auto& v : item.vertices) v.color.a = occlusion[at++] / 255.f;
            item.mesh->meshBuffers = engine->uploadMesh(item.indices, item.vertices);
        }
    }

    return scene;
}
