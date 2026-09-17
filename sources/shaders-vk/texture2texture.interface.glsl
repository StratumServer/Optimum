// Program interface of texture2texture (docs/vulkan-native-shaders.md section 4). One draw per Use(), so the push
// block holds only the sampler slot and every other uniform is a record member: texture2texture.vsh's, then
// texture2texture.fsh's, each in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float xs;
    float ys;
    float width;
    float height;

    float texu;
    float texv;
    float texw;
    float texh;
    float alphaTest;
};
