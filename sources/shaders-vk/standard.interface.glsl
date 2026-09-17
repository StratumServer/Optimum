// Program interface of standard (docs/vulkan-native-shaders.md section 4). Held items, dropped items and
// block-entity models: at most a few draws per Use(), so the push block holds the two sampler slots (tex, then
// tex2dOverlay, the GLSL 330 unit order) and the integer flags plus taaReactive, in declaration order (vertex
// stage first). The record holds everything else: standard.vsh's uniforms, vertexwarp.glsl's prev* uniforms,
// standard.fsh's uniforms and underwatereffects.glsl's frameSize, each in declaration order. Names inside the
// TAAMOTION and ALLOWDEPTHOFFSET blocks are declared unconditionally (collectUniformNames reads the
// unpreprocessed text).
//
// A block member cannot carry the GLSL 330 initializers (taaHistoryValid = 0, applySsao = 1,
// taaReactive = 0.0, extraGodray = 0, alphaTest = 0.001, ssaoAttn = 0, damageEffect = 0, and vertexwarp's
// prev* defaults); the runtime seeds them (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2dOverlay);

    int extraGlow;
    int dontWarpVertices;
    int fadeFromSpheresFog;
    int addRenderFlags;
    int taaHistoryValid;

    int applySsao;
    int tempGlowMode;
    int normalShaded;
    int skyShaded;
    float taaReactive;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaTint;
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaGlowIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 viewMatrix;
    mat4 modelMatrix;
    float extraZOffset;
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

    float extraGodray;
    float alphaTest;
    float ssaoAttn;
    float overlayOpacity;
    vec2 overlayTextureSize;
    vec2 baseTextureSize;
    vec2 baseUvOrigin;
    float damageEffect;
    float depthOffset;
    vec4 averageColor;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    vec2 frameSize;
};
