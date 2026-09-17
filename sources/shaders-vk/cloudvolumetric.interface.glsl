// Program interface of cloudvolumetric (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw
// per Use(), so the push block holds only the sampler slots, in cloudvolumetric.fsh's declaration order, and
// every other uniform is a record member in declaration order.
//
// liquidDepth, which cloudvolumetric.fsh declares itself between cloudCol and cloudMapWidth, is the set 0 frame
// texture bindings.glsl declares (section 2, "Oracle decisions"), so it has no slot here.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, cloudMap);
    OPTIMUM_SAMPLER_SLOT(sampler2D, cloudCol);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 iMvpMatrix;
    float cloudMapWidth;
    vec3 cloudOffset;
    int frame;
    float time;
    int FrameWidth;
    float PerceptionEffectIntensity;
};
