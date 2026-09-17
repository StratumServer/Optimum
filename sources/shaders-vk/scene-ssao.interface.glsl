// Program interface of scene-ssao (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots (GLSL 330 declaration order) and every other uniform is
// a record member.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, ssaoScene);
    OPTIMUM_SAMPLER_SLOT(sampler2D, gPositionScene);
    OPTIMUM_SAMPLER_SLOT(sampler2D, revealageScene);
    OPTIMUM_SAMPLER_SLOT(sampler2D, aoAlbedo);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float invRenderHeight;
    int optimumAoMode;
};
