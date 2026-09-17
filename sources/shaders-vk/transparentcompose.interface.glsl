// Program interface of transparentcompose (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one
// draw per Use(), so the push block holds only the sampler slots, in transparentcompose.fsh's declaration
// order, and there is no program record.
//
// OITaccumulation is the fifth slot, the unit collectUniformNames assigns to the name `Array` it misreads
// from `uniform sampler2DArray OITaccumulation` (docs/vulkan-native-shaders.md, "Family post").
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, accumulation);
    OPTIMUM_SAMPLER_SLOT(sampler2D, revealage);
    OPTIMUM_SAMPLER_SLOT(sampler2D, inGlow);
    OPTIMUM_SAMPLER_SLOT(sampler2D, OITreveal);
    OPTIMUM_SAMPLER_SLOT(sampler2DArray, OITaccumulation);
};
