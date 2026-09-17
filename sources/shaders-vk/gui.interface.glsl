// Program interface of gui (docs/vulkan-native-shaders.md section 4, GUI row). The push block holds the two
// sampler slots in gui.fsh's declaration order, then the per-element uniforms RenderAPIGame.RenderRectangle
// sets on every rectangle (rgbaIn, extraGlow, applyColor, noTexture, overlayOpacity). The record holds
// projectionMatrix, modelViewMatrix, modelMatrix and every other uniform: gui.vsh's in declaration order,
// then gui.fsh's, then normalshading.fsh's lightPosition (its owner fogandlight.fsh is not included).
//
// The GLSL 330 initializers (sepiaLevel = 0, damageEffect = 0) are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2dOverlay);
    vec4 rgbaIn;
    int extraGlow;
    int applyColor;
    float noTexture;
    float overlayOpacity;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaGlowIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    mat4 modelMatrix;
    int applyModelMat;
    int applyAnimation;

    float alphaTest;
    int darkEdges;
    int tempGlowMode;
    int transparentCenter;
    int normalShaded;
    float sepiaLevel;
    float damageEffect;
    vec2 overlayTextureSize;
    vec2 baseTextureSize;
    vec2 baseUvOrigin;

    vec3 lightPosition;
};
