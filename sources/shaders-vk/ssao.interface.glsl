// Program interface of ssao (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in ssao.fsh's declaration order, and every other
// uniform is a record member in declaration order.
//
// temporalFrameIndex sits inside `#if TAAMOTION == 1` in ssao.fsh. collectUniformNames sees it in every
// variant, and the record keeps one layout per program, so it is declared unconditionally; only the code
// that reads it stays behind the TAAMOTION axis.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, gPosition);
    OPTIMUM_SAMPLER_SLOT(sampler2D, gNormal);
    OPTIMUM_SAMPLER_SLOT(sampler2D, texNoise);
    OPTIMUM_SAMPLER_SLOT(sampler2D, revealage);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec3 samples[64];
    vec2 screenSize;
    mat4 projection;
    float temporalFrameIndex;
};
