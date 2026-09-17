// Program interface of aurora (docs/vulkan-native-shaders.md section 4). One draw per Use(): the push block
// holds only the sampler slot; every other uniform is a record member: aurora.vsh's, aurora.fsh's (its second
// auroraCounter is the same name), then fogandlight.fsh's windWaveCounter (its owner vertexwarp.vsh is not
// included).
//
// extraGodray = 0 and alphaTest = 0.001 are GLSL 330 initializers; the runtime seeds them (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 color;
    vec4 rgbaTint;
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaBlockIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    float auroraCounter;

    float extraGodray;
    float alphaTest;

    float windWaveCounter;
};
