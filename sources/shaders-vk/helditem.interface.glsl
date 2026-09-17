// Program interface of helditem (docs/vulkan-native-shaders.md section 4). At most a couple of draws per Use(),
// so the push block holds only the sampler slots, in helditem.fsh's declaration order. The record holds
// helditem.vsh's uniforms, then helditem.fsh's, then normalshading.fsh's lightPosition (its owner
// fogandlight.fsh is not included), each in declaration order.
// The GLSL 330 initializers (alphaTest = 0.001, damageEffect = 0) are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, itemTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2dOverlay);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaGlowIn;
    int extraGlow;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float alphaTest;
    float overlayOpacity;
    vec2 overlayTextureSize;
    vec2 baseTextureSize;
    vec2 baseUvOrigin;
    int normalShaded;
    float damageEffect;

    vec3 lightPosition;
};
