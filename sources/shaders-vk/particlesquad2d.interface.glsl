// Program interface of particlesquad2d (docs/vulkan-native-shaders.md section 4). Particles have no DRAW
// uniforms, so the push block holds only the sampler slot; everything else is a record member,
// particlesquad2d.vsh's then particlesquad2d.fsh's, each in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, particleTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    int oitPass;
    int withTexture;
    int heldItemMode;
};
