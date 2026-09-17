// Program interface of bilateralblur (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw
// per Use(), so the push block holds only the sampler slots and bilateralblur.vsh's two uniforms are the record.
//
// bilateralblur.fsh also declares an input named frameSize that no vertex stage writes and nothing reads.
// The record's frameSize is a global name in both stages, so that input would redeclare it: the fragment
// stage drops it (docs/vulkan-native-shaders.md, "Family post").
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, inputTexture);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTexture);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 frameSize;
    int isVertical;
};
