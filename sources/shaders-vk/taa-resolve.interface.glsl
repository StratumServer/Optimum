// Program interface of taa-resolve (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in taa-resolve.fsh's declaration order, and every other
// uniform is a record member in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, sceneTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, glowTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, motionTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyColor);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyGlow);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyDepth);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 renderSize;
    vec2 jitterPx;
    mat4 invViewProjJittered;
    mat4 prevViewProj;
    mat4 viewMatrix;
    vec3 cameraDelta;
    int resetHistory;
    float blendAlpha;
    float varianceGamma;
};
