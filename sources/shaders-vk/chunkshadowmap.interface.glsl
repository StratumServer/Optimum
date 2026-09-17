// Program interface of chunkshadowmap (docs/vulkan-native-shaders.md section 4). One draw per mesh pool
// per cascade: the push block holds the sampler slot, then origin and mvpMatrix (80 B). The record holds
// the subpixel padding and the previous-frame warp state vertexwarp.glsl reads.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
    vec3 origin;
    mat4 mvpMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
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
};
