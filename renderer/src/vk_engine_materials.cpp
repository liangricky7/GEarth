#include "vk_engine.h"
#include "vk_pipelines.h"
#include "vk_initializers.h"

void GLTFMetallic_Roughness::build_pipelines(VulkanEngine* engine) {
    DescriptorLayoutBuilder descriptors;
    descriptors.add_binding(0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
    for (int i = 1; i <= 3; ++i) descriptors.add_binding(i, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    materialLayout = descriptors.build(engine->_device, VK_SHADER_STAGE_FRAGMENT_BIT);
    VkDescriptorSetLayout sets[] = {engine->_gpuSceneDataDescriptorLayout, materialLayout};
    VkPushConstantRange push{VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(GPUDrawPushConstants)};
    auto layout = vkinit::pipeline_layout_create_info();
    layout.setLayoutCount = 2; layout.pSetLayouts = sets;
    layout.pushConstantRangeCount = 1; layout.pPushConstantRanges = &push;
    VK_CHECK(vkCreatePipelineLayout(engine->_device, &layout, nullptr, &opaquePipeline.layout));
    maskPipeline.layout = transparentPipeline.layout = opaquePipeline.layout;
    ShaderModules shaders(engine->_device);
    auto vert = shaders.load(engine->shader_path("mesh.vert.spv"));
    auto frag = shaders.load(engine->shader_path("mesh.frag.spv"));
    PipelineBuilder builder;
    builder._pipelineLayout = opaquePipeline.layout;
    builder.set_shaders(vert, frag);
    builder.set_input_topology(VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
    builder.set_polygon_mode(VK_POLYGON_MODE_FILL);
    builder.set_cull_mode(VK_CULL_MODE_NONE, VK_FRONT_FACE_COUNTER_CLOCKWISE);
    builder.set_multisampling_none();
    builder.disable_blending();
    builder.enable_depthtest(true, VK_COMPARE_OP_GREATER_OR_EQUAL);
    builder.set_color_attachment_format(engine->colorFormat);
    builder.set_depth_format(engine->depthFormat);
    opaquePipeline.pipeline = builder.build_pipeline(engine->_device);
    maskPipeline.pipeline = opaquePipeline.pipeline;
    builder.enable_blending_alphablend();
    builder.enable_depthtest(false, VK_COMPARE_OP_GREATER_OR_EQUAL);
    transparentPipeline.pipeline = builder.build_pipeline(engine->_device);
    if (!opaquePipeline.pipeline || !transparentPipeline.pipeline) throw std::runtime_error("Mesh pipeline creation failed");
}

void GLTFMetallic_Roughness::clear_resources(VkDevice device) {
    vkDestroyPipeline(device, opaquePipeline.pipeline, nullptr);
    vkDestroyPipeline(device, transparentPipeline.pipeline, nullptr);
    vkDestroyPipelineLayout(device, opaquePipeline.layout, nullptr);
    vkDestroyDescriptorSetLayout(device, materialLayout, nullptr);
}

MaterialInstance GLTFMetallic_Roughness::write_material(VkDevice device, MaterialPass pass,
    const MaterialResources& resources, DescriptorAllocatorGrowable& allocator) {
    MaterialInstance material;
    material.passType = pass;
    material.pipeline = pass == MaterialPass::Transparent ? &transparentPipeline :
        pass == MaterialPass::Mask ? &maskPipeline : &opaquePipeline;
    material.materialSet = allocator.allocate(device, materialLayout);
    write_material_set(device, resources, material.materialSet);
    return material;
}

void GLTFMetallic_Roughness::write_material_set(VkDevice device, const MaterialResources& r, VkDescriptorSet set) {
    writer.clear();
    writer.write_buffer(0, r.dataBuffer, sizeof(MaterialConstants), r.dataBufferOffset, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
    writer.write_image(1, r.colorImage.imageView, r.colorSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.write_image(2, r.metalRoughImage.imageView, r.metalRoughSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.write_image(3, r.normalImage.imageView, r.normalSampler, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    writer.update_set(device, set);
}

void MeshNode::Draw(const glm::mat4& topMatrix, DrawContext& ctx) {
    if (!mesh || !mesh->meshBuffers.indexBuffer.buffer) { Node::Draw(topMatrix, ctx); return; }
    glm::mat4 nodeMatrix = topMatrix * worldTransform;
    for (auto& s : mesh->surfaces) {
        if (!s.material) continue;
        RenderObject def;
        def.indexCount = s.count;
        def.firstIndex = s.startIndex;
        def.indexBuffer = mesh->meshBuffers.indexBuffer.buffer;
        def.material = &s.material->data;
        def.bounds = s.bounds;
        def.transform = nodeMatrix;
        def.vertexBufferAddress = mesh->meshBuffers.vertexBufferAddress;
        if (s.material->data.passType == MaterialPass::Transparent) {
            ctx.TransparentSurfaces.push_back(def);
        } else {
            ctx.OpaqueSurfaces.push_back(def);
        }
    }
    Node::Draw(topMatrix, ctx);
}
