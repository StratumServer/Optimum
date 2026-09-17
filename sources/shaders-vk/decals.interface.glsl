// Program interface of decals (docs/vulkan-native-shaders.md section 4). Decals are pooled like chunks:
// the push block holds the two sampler slots (decals.fsh order) and the DRAW uniforms origin and
// modelViewMatrix (84 B). The record holds the rest: decals.vsh's, vertexwarp.vsh's previous-frame
// mirrors, then decals.fsh's, each in declaration order. The TAA uniforms are declared in every variant.
// The prev* initializers are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, decalTexture);
    OPTIMUM_SAMPLER_SLOT(sampler2D, blockTexture);
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogDensityIn;
    float fogMinIn;
    mat4 projectionMatrix;
    mat4 prevProjectionMatrix;
    mat4 prevModelViewMatrix;
    vec3 cameraPosDelta;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;

    vec2 taaRenderSize;
    vec2 taaJitterPx;
};
