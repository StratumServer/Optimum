// Program interface of guitopsoil (docs/vulkan-native-shaders.md section 4, GUI row): the sampler slot, then
// the per-element GUI uniforms (rgbaIn, extraGlow, applyColor, noTexture) in the push block; the matrices,
// blockTextureSize and alphaTest in the record, each stage in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    vec4 rgbaIn;
    int extraGlow;
    int applyColor;
    float noTexture;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float blockTextureSize;
    float alphaTest;
};
