// Program interface of luma (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slot and there is no program record.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, scene);
};
