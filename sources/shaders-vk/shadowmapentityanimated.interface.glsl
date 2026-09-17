// Program interface of shadowmapentityanimated (docs/vulkan-native-shaders.md section 4). The shadow pass
// sets modelViewMatrix per entity (EntityShapeRenderer's isShadowPass branch) and addRenderFlags; with the
// sampler slot they fit the push block (72 B). projectionMatrix is set once per shadow map and goes to the
// record.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, entityTex);

    mat4 modelViewMatrix;
    int addRenderFlags;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
};
