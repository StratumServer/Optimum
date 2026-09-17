// Program interface of chunkliquiddepth (docs/vulkan-native-shaders.md section 4). One draw per mesh pool:
// the push block holds origin and modelViewMatrix (76 B). The program includes no owner of viewDistance
// (fogandlight.vsh), so it is a record member here, with projectionMatrix and the previous-frame warp state
// vertexwarp.glsl reads.
layout(push_constant, scalar) uniform OptimumDraw
{
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    float viewDistance;

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
