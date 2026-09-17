// Program interface of nightsky (docs/vulkan-native-shaders.md section 4). One draw per Use(): the push
// block holds only the cube map's slot; every other uniform is a record member: nightsky.vsh's,
// nightsky.fsh's (ditherSeed, horizontalResolution and playerToSealevelOffset are its own here, because
// skycolor.fsh, their frame owner, is not included), then fogandlight.fsh's windWaveCounter and
// underwatereffects.fsh's frameSize.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(samplerCube, ctex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelMatrix;
    mat4 viewMatrix;

    vec4 rgbaFog;
    int ditherSeed;
    int horizontalResolution;
    float dayLight;
    float horizonFog;
    float playerToSealevelOffset;
    float fogDensityIn;
    float fogMinIn;

    float windWaveCounter;
    vec2 frameSize;
};
