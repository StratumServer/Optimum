// Program interface of taa-skymotion (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw
// per Use(), so the push block holds only the sampler slot and every other uniform is a record member.
//
// The GLSL 330 source declares all of these inside #if TAAMOTION > 0; the oracle reads the unpreprocessed
// text, so they are names of every variant and are declared unconditionally here. taaCloudReactive's GLSL 330
// initializer (1.0) is seeded by the runtime (docs/vulkan-native-shaders.md section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, transparentRevealTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 taaRenderSize;
    vec2 taaJitterPx;
    mat4 taaInvViewProjJittered;
    mat4 taaPrevViewProj;
    float taaCloudReactive;
};
