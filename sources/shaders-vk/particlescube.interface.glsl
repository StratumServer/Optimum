// Program interface of particlescube (docs/vulkan-native-shaders.md section 4). Particles have no DRAW
// uniforms and this program samples nothing, so there is no push block; every uniform is a record member:
// particlescube.vsh's, vertexwarp.vsh's previous-frame mirrors, then particlescube.fsh's and
// underwatereffects.fsh's, each in declaration order. The TAA uniforms are declared in every variant
// because collectUniformNames sees them whatever TAAMOTION is.
//
// The prev* warp uniforms carry GLSL 330 initializers (prevWindWaveIntensity = 1, ...); the runtime seeds
// them from the GLSL 330 declarations (docs/vulkan-native-shaders.md section 8).
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
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

    vec2 frameSize;
};
