// Program interface of godrays (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots and every other uniform is a record member:
// godrays.vsh's seven, then godrays.fsh's one, each in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, inputTexture);
    OPTIMUM_SAMPLER_SLOT(sampler2D, glowParts);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 invFrameSizeIn;
    vec3 sunPosScreenIn;
    vec3 sunPos3dIn;
    vec3 playerViewVector;
    float iGlobalTimeIn;
    float directionIn;
    int dusk;

    int maxGodRaySamples;
};
