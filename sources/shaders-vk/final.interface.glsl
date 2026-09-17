// Program interface of final (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in final.fsh's declaration order, and every other
// uniform is a record member: final.vsh's four, then final.fsh's, each in declaration order.
//
// A block member cannot carry the GLSL 330 initializers (extraGamma = 1.0, minlight = 0.0, maxlight = 1,
// minsat = 0, maxsat = 1). The client never sets minlight, maxlight, minsat or maxsat, so the runtime seeds
// the record from the GLSL 330 declarations' initializers (docs/vulkan-native-shaders.md section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, primaryScene);
    OPTIMUM_SAMPLER_SLOT(sampler2D, glowParts);
    OPTIMUM_SAMPLER_SLOT(sampler2D, bloomParts);
    OPTIMUM_SAMPLER_SLOT(sampler2D, godrayParts);
    OPTIMUM_SAMPLER_SLOT(sampler2D, ssaoScene);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 invFrameSizeIn;
    vec3 sunPosScreenIn;
    vec3 sunPos3dIn;
    vec3 playerViewVector;

    int optimumSsaoInScene;
    int optimumAoDebug;

    float gammaLevel;
    float brightnessLevel;
    float contrastLevel;
    float sepiaLevel;
    float ambientBloomLevel;
    float damageVignetting;
    float damageVignettingSide;
    float frostVignetting;
    float extraGamma;
    float windWaveCounter;
    float glitchEffectStrength;

    float minlight;
    float maxlight;
    float minsat;
    float maxsat;
};
