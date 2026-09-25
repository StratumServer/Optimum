#version 330 core
uniform sampler2D ssaoScene;
uniform float invRenderHeight;
#if OPTIMUMAO > 0
// Optimum AO (docs/vulkan.md#ambient-occlusion C.9, C.11): with GTAO the AO texture is the
// pure visibility at render resolution, so the water, fog and OIT attenuation vanilla SSAO
// applies inside its own pass is applied here instead.
uniform sampler2D gPositionScene;
uniform sampler2D revealageScene;
// 1: ssaoScene is the GTAO visibility; 0: vanilla SSAO's blurred result (a frame GTAO stood down).
uniform int optimumAoMode;
#endif
#if OPTIMUMAO_MULTIBOUNCE > 0
// The albedo hook (C.11): GTAO 2016 eq. 10 needs the surface albedo, which the lit LDR scene
// colour is not. Never stamped in the first version; the platform refuses the tone without
// an albedo texture (GtaoSettings.EffectiveTone).
uniform sampler2D aoAlbedo;
float optimumMultiBounce(float visibility, vec3 albedo)
{
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    vec3 bounced = max(vec3(visibility), ((visibility * a + b) * visibility + c) * visibility);
    // The Multiply blend carries one factor; a coloured term needs a colour multiply blend.
    return dot(bounced, vec3(0.2126, 0.7152, 0.0722));
}
#endif
in vec2 texCoord;
layout(location = 0) out vec4 outColor;
void main()
{
    float ao = texture(ssaoScene, texCoord).r;
#if OPTIMUMAO > 0
    if (optimumAoMode == 1)
    {
        // Same resolution as Primary: nearest texel, no upsample and no min-of-two-rows.
        ivec2 texel = ivec2(gl_FragCoord.xy);
        ao = texelFetch(ssaoScene, texel, 0).r;
    #if OPTIMUMAO_MULTIBOUNCE > 0
        ao = optimumMultiBounce(ao, texelFetch(aoAlbedo, texel, 0).rgb);
    #endif
        // vanilla ssao.fsh: attenuate = gPosition.w + 0.75 * (1 - revealage), occ = 1 - (1 - ao) * (1 - attenuate)
        float attenuate = texelFetch(gPositionScene, texel, 0).w +
            max(0.0, 1.0 - texelFetch(revealageScene, texel, 0).r) * 0.75;
        ao = 1.0 - (1.0 - ao) * (1.0 - attenuate);
    }
    else
#endif
    {
    #if SSAOLEVEL > 1
        ao = min(ao, texture(ssaoScene, texCoord - vec2(0.0, invRenderHeight)).r);
    #endif
    }
    // EnumBlendMode.Multiply: dstRGB * (1 - srcAlpha). RGB is not read.
    outColor = vec4(0.0, 0.0, 0.0, 1.0 - clamp(ao, 0.0, 1.0));
}
