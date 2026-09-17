#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of scene-ssao.fsh (docs/vulkan-native-shaders.md). The SSAOLEVEL > 1 and OPTIMUMAO > 0 preprocessor
// branches are specialization-constant branches with the same expressions; they gate no output, so no variant
// axes. OPTIMUMAO_MULTIBOUNCE is never stamped by ShaderRegistry: it is a native constant fixed to 0 here, so the
// multibounce code compiles out while aoAlbedo stays declared as the oracle sees it (section 5).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "scene-ssao.interface.glsl"

const int OPTIMUM_AO_MULTIBOUNCE = 0;

// The albedo hook (C.11): GTAO 2016 eq. 10 needs the surface albedo, which the lit LDR scene
// colour is not. Never stamped in the first version; the platform refuses the tone without
// an albedo texture (GtaoSettings.EffectiveTone).
float optimumMultiBounce(float visibility, vec3 albedo)
{
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    vec3 bounced = max(vec3(visibility), ((visibility * a + b) * visibility + c) * visibility);
    // The Multiply blend carries one factor; a coloured term needs a colour multiply blend.
    return dot(bounced, vec3(0.2126, 0.7152, 0.0722));
}

layout(location = 0) in vec2 texCoord;
layout(location = 0) out vec4 outColor;
void main()
{
    float ao = texture(optimumTextures2D[ssaoScene], texCoord).r;
    // GLSL 330: `#if OPTIMUMAO > 0 if (optimumAoMode == 1) { ... } else #endif { ... }`; the same control flow.
    if (OPTIMUM_OPTIMUMAO > 0 && optimumAoMode == 1)
    {
        // Same resolution as Primary: nearest texel, no upsample and no min-of-two-rows.
        ivec2 texel = ivec2(gl_FragCoord.xy);
        ao = texelFetch(optimumTextures2D[ssaoScene], texel, 0).r;
        if (OPTIMUM_AO_MULTIBOUNCE > 0) {
        ao = optimumMultiBounce(ao, texelFetch(optimumTextures2D[aoAlbedo], texel, 0).rgb);
        }
        // vanilla ssao.fsh: attenuate = gPosition.w + 0.75 * (1 - revealage), occ = 1 - (1 - ao) * (1 - attenuate)
        float attenuate = texelFetch(optimumTextures2D[gPositionScene], texel, 0).w +
            max(0.0, 1.0 - texelFetch(optimumTextures2D[revealageScene], texel, 0).r) * 0.75;
        ao = 1.0 - (1.0 - ao) * (1.0 - attenuate);
    }
    else
    {
    if (OPTIMUM_SSAOLEVEL > 1) {
        ao = min(ao, texture(optimumTextures2D[ssaoScene], texCoord - vec2(0.0, invRenderHeight)).r);
    }
    }
    // EnumBlendMode.Multiply: dstRGB * (1 - srcAlpha). RGB is not read.
    outColor = vec4(0.0, 0.0, 0.0, 1.0 - clamp(ao, 0.0, 1.0));
}
