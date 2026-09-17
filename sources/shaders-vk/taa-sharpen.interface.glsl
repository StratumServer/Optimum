// Program interface of taa-sharpen (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slot and every other uniform is a record member.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, inputScene);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 inputTexelSize;
    float sharpness;
};
