// Program interface of debugdepthbuffer (docs/vulkan-native-shaders.md section 4). One draw per Use(), so the
// push block holds only the sampler slot and debugdepthbuffer.vsh's two matrices are the record.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthSampler);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
};
