// Program interface of celestialobject (docs/vulkan-native-shaders.md section 4). SystemRenderSunMoon draws
// once per Use(), so the push block holds only the sampler slot; every other uniform is a record member:
// celestialobject.vsh's, celestialobject.fsh's, then fogandlight.fsh's windWaveCounter (its owner
// vertexwarp.vsh is not included) and underwatereffects.fsh's frameSize.
//
// extraGodray = 0 and alphaTest = 0.001 are GLSL 330 initializers; the runtime seeds them (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    mat4 projectionMatrix;
    mat4 modelMatrix;
    mat4 viewMatrix;
    int extraGlow;

    float extraGodray;
    float alphaTest;
    float fogDensityIn;
    float fogMinIn;
    float horizonFog;
    vec3 sunPosition;
    vec3 moonPosition;
    float moonSunAngle;
    int weirdMathToMakeMoonLookNicer;
    float dayLight;

    float windWaveCounter;
    vec2 frameSize;
};
