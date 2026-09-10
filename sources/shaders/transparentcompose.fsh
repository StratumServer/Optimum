#version 330 core

// Optimum override of the vanilla transparentcompose.fsh (TAA P4): the OIT
// merge, plus the reactive value for everything that was drawn into the
// Transparent target.
//
// Quad particles, OIT entities, liquid shading and anything else that goes
// through oit.fsh cannot write Primary's motion attachment - their six OIT
// outputs already fill the Transparent target's attachment set - so
// TAA-PLAN.md's Conventions give them their reactive value here instead:
// "OIT transparents `1 - revealage` from the merge". This pass is the one place
// in the frame where the total coverage of all that transparent content over a
// pixel is known: `anet` below, which vanilla already computes and hands to
// outColor's alpha for the blend that follows.
//
// Only the blue channel of the motion attachment is meant to change. The rg
// (the vector) and a (the writer depth) of whatever opaque surface wrote this
// pixel have to survive, or the transparent content in front would delete the
// motion of the geometry behind it. That is done with blending rather than a
// colour mask: ClientPlatformWindows.MergeTransparentRenderPass puts the motion
// attachment on FUNC_ADD with (ONE, ONE), and the fragment writes zero into rg
// and a, so those channels add zero and only b accumulates. Where nothing
// transparent covers the pixel anet is 0 and the attachment is bit-for-bit
// unchanged.


uniform sampler2D accumulation;
uniform sampler2D revealage;
uniform sampler2D inGlow;

uniform sampler2D OITreveal;
uniform sampler2DArray OITaccumulation;

in vec2 v_texcoord;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#if TAAMOTION > 0
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
#endif

#define OIT_BINS 3

vec3 unproject(vec4 a){
    return a.w < 0.0001 ? vec3(0.0) : a.xyz / a.w;
}

void main(){

    vec4 reveal = 1.0 - texelFetch(OITreveal, ivec2(gl_FragCoord), 0);
    float anet = 1.0 - texelFetch(revealage, ivec2(gl_FragCoord), 0).r;
    vec4 k = vec4(0.0);
    float a = 1.0;

    for(int i = 0; i < OIT_BINS; i++){

        vec4 bin = texelFetch(OITaccumulation, ivec3(gl_FragCoord.xy, i), 0);
        float anet_k = reveal[i];

        k += vec4(unproject(bin) * anet_k, anet_k) * a;
        a *= 1.0 - anet_k;

    }

    outColor = vec4(unproject(k), anet);
    outGlow = texture(inGlow, v_texcoord);

#if TAAMOTION > 0
    // anet is the fraction of this pixel the transparent layer covers - the
    // very alpha this pass is composited with. It is the reactive value: at 1
    // the pixel is entirely transparent content with no motion vector of its
    // own, at 0 the pixel is untouched by it.
    outMotion = vec4(0.0, 0.0, clamp(anet, 0.0, 1.0), 0.0);
#endif

}
