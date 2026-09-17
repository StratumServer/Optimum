// Program interface of colorgrade (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slot and every other uniform is a record member:
// colorgrade.vsh's one, then colorgrade.fsh's, each in declaration order.
//
// A block member cannot carry the GLSL 330 initializers (minlight = 0.0, maxlight = 1, minsat = 0,
// maxsat = 1); the runtime seeds the record from the GLSL 330 declarations (docs/vulkan-native-shaders.md
// section 8), as for final.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, primaryScene);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 invFrameSizeIn;

    float gammaLevel;
    float brightnessLevel;
    float sepiaLevel;
    float damageVignetting;

    float minlight;
    float maxlight;
    float minsat;
    float maxsat;
};
