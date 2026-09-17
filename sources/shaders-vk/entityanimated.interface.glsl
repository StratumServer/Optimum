// Program interface of entityanimated (docs/vulkan-native-shaders.md section 4), shared by the opaque
// program, Entityanimated_Oit (USEOIT axis) and the first-person hands (ALLOWDEPTHOFFSET axis).
//
// Entity placement (section 4, about 280 B of DRAW data per entity): the push block holds the sampler slot,
// then the small per-entity integers and the TAA flags in declaration order (vertex stage first). The record
// holds the matrices, the colours and every remaining scalar: entityanimated.vsh's uniforms, the prev*
// uniforms of vertexwarp.glsl, then entityanimated.fsh's uniforms and underwatereffects.glsl's frameSize, each
// in declaration order. Names the TAAMOTION, USEOIT and ALLOWDEPTHOFFSET blocks declared are declared
// unconditionally: collectUniformNames reads the unpreprocessed text, so every variant has all of them.
//
// A block member cannot carry the GLSL 330 initializers (frostAlpha = 0, taaHistoryValid = 0,
// taaReactive = 0.0, alphaTest = 0.001, and vertexwarp's prev* defaults); the runtime seeds them
// (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, entityTex);

    int addRenderFlags;
    int extraGlow;
    int taaHistoryValid;

    float taaReactive;
    int entityId;
    int glitchFlicker;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    vec4 renderColor;
    float frostAlpha;
    mat4 projectionMatrix;
    mat4 viewMatrix;
    mat4 modelMatrix;
    int skipRenderJointId;
    int skipRenderJointId2;
    mat4 prevProjectionMatrix;
    mat4 prevViewMatrix;
    mat4 prevModelMatrix;
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
    float alphaTest;
    float glitchEffectStrength;
    float depthOffset;

    vec2 frameSize;
};
