// Program interface of guigear (docs/vulkan-native-shaders.md section 4, GUI row): the sampler slot in the
// push block; the matrices and guigear.fsh's scalars in the record, each stage in declaration order.
// stabilityLevel's GLSL 330 initializer (0.5) is seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float gearCounter;
    float stabilityLevel;
    float shadeYPos;
    float hotbarYPos;
    float gearHeight;
};
