// Program interface of wireframe (docs/vulkan-native-shaders.md section 4). No samplers, so there is no push block:
// the record holds wireframe.vsh's uniforms in declaration order, then vertexwarp.vsh's prev* uniforms in its
// header's order (their owner rule: they are program uniforms, not frame members). The prev* GLSL 330
// initializers (some are 1) are seeded by the runtime (section 8).
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    vec4 colorIn;
    vec3 origin;
    float extraGlow;

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
