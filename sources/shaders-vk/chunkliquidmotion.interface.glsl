// Program interface of chunkliquidmotion (docs/vulkan-native-shaders.md section 4). A chunk-family program: one
// draw per mesh pool per Use(), so the DRAW uniforms origin and modelViewMatrix sit in the push block (76 B, no
// samplers). The record holds the rest: chunkliquidmotion.vsh's uniforms in declaration order, then the
// vertexwarp.glsl optimum-program-uniform names (vertexwarp.vsh owns only the current-frame members; the prev*
// mirrors are program uniforms), then chunkliquidmotion.fsh's.
//
// The GLSL 330 sources declare prevProjectionMatrix, prevModelViewMatrix, cameraPosDelta, taaRenderSize,
// taaJitterPx and taaLiquidReactive inside #if TAAMOTION > 0; the oracle reads the unpreprocessed text, so they
// are names of every variant and are declared unconditionally. The GLSL 330 initializers (taaLiquidReactive
// = 0.3 and vertexwarp's prev* defaults) are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
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
    float taaLiquidReactive;
};
