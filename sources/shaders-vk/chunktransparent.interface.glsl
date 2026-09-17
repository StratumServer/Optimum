// Program interface of chunktransparent (docs/vulkan-native-shaders.md section 4). One draw per mesh pool:
// the push block holds the sampler slot, then origin, modelViewMatrix and forcedTransparency (84 B). The
// record holds every other uniform, chunktransparent.vsh's first (the previous-frame warp state
// vertexwarp.glsl reads comes with its include), then underwatereffects' frameSize.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    vec3 origin;
    mat4 modelViewMatrix;
    float forcedTransparency;
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

    vec2 frameSize;
};
