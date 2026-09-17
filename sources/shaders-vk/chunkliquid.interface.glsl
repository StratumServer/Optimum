// Program interface of chunkliquid (docs/vulkan-native-shaders.md section 4). One draw per mesh pool: the
// push block holds the two sampler slots in chunkliquid.fsh's declaration order, then origin and
// modelViewMatrix (84 B). The record holds every other uniform, chunkliquid.vsh's first (the previous-frame
// warp state vertexwarp.glsl reads comes with its include), then chunkliquid.fsh's and underwatereffects'
// frameSize. blockTextureSize and sunPosRel, declared by both stages, appear once.
//
// waterWaveCounter and windSpeed, which chunkliquid.fsh declares itself, are frame members here (their owner
// vertexwarp.vsh is included by the vertex stage). A block member cannot carry chunkliquid.fsh's initializer
// (dropletIntensity = 0); the runtime seeds the record from the GLSL 330 declarations (contract section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float waterStillCounter;
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogDensityIn;
    float fogMinIn;
    mat4 projectionMatrix;
    vec2 blockTextureSize;
    vec3 playerViewVec;
    vec3 sunPosRel;
    vec3 playerPosForFoam;
    float subpixelPaddingX;
    float subpixelPaddingY;

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

    vec2 textureAtlasSize;
    float waterFlowCounter;
    vec3 sunColor;
    vec3 reflectColor;
    float sunSpecularIntensity;
    float dropletIntensity;

    vec2 frameSize;
};
