// Program interface of taa-debug (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in taa-debug.fsh's declaration order, and every other
// uniform is a record member.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, motionTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sceneTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    int mode;
    vec2 renderSize;
};
