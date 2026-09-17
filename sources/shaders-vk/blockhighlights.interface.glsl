// Program interface of blockhighlights (docs/vulkan-native-shaders.md section 4). The push block holds the
// sampler slot. The record holds the matrices and fogandlight.fsh's windWaveCounter, whose owner
// (vertexwarp.vsh) the program does not include; fogandlight.fsh's other program uniforms are frame members
// through fogandlight.vsh in the vertex stage.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, particleTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float windWaveCounter;
};
