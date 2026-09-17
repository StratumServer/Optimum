// Program interface of chunkopaque (docs/vulkan-native-shaders.md section 4). One draw per mesh pool: the
// push block holds the two sampler slots in chunkopaque.fsh's declaration order, then origin and
// modelViewMatrix (84 B). The record holds every other uniform, chunkopaque.vsh's first (the previous-frame
// warp state vertexwarp.glsl reads comes with its include), then chunkopaque.fsh's and underwatereffects'
// frameSize.
//
// Every uniform is declared whatever the axes: the GLSL 330 name set does not depend on defines.
// cameraUnderwater, shadowIntensity and lightPosition, which chunkopaque.vsh declares itself, are frame
// members here (their owners underwatereffects.fsh and fogandlight.fsh are included by the fragment stage).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTexLinear);
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
    float subpixelPaddingX;
    float subpixelPaddingY;
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

    float alphaTest;
    float horizonFog;
    vec3 sunPosition;
    float dayLight;
    int haxyFade;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    vec2 frameSize;
};
