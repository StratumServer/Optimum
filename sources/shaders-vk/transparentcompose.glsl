#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of transparentcompose.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of transparentcompose.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
// GBUFFER and TAAMOTION are variant axes: they gate outputs. The motion attachment is written through
// optimumWriteReactiveOnly under the merge's additive (ONE, ONE) blend, so rg and a add zero and only b
// accumulates, exactly as the GLSL 330 override does.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of transparentcompose (docs/vulkan.md). A fullscreen pass: one
// draw per Use(), so the push block holds only the sampler slots, in transparentcompose.fsh's declaration
// order, and there is no program record.
//
// OITaccumulation is the fifth slot, the unit collectUniformNames assigns to the name `Array` it misreads
// from `uniform sampler2DArray OITaccumulation` (docs/vulkan.md, "Family post").
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, accumulation);
    OPTIMUM_SAMPLER_SLOT(sampler2D, revealage);
    OPTIMUM_SAMPLER_SLOT(sampler2D, inGlow);
    OPTIMUM_SAMPLER_SLOT(sampler2D, OITreveal);
    OPTIMUM_SAMPLER_SLOT(sampler2DArray, OITaccumulation);
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) out vec2 v_texcoord;

void main(void)
{
	// https://rauwendaal.net/2014/06/14/rendering-a-screen-covering-triangle-in-opengl/
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    v_texcoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "motion.glsl"

layout(location = 0) in vec2 v_texcoord;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#if TAAMOTION == 1
#if GBUFFER
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#endif

#define OIT_BINS 3

vec3 unproject(vec4 a){
    return a.w < 0.0001 ? vec3(0.0) : a.xyz / a.w;
}

void main(){

    vec4 reveal = 1.0 - texelFetch(optimumTextures2D[OITreveal], ivec2(gl_FragCoord), 0);
    float anet = 1.0 - texelFetch(optimumTextures2D[revealage], ivec2(gl_FragCoord), 0).r;
    vec4 k = vec4(0.0);
    float a = 1.0;

    for(int i = 0; i < OIT_BINS; i++){

        vec4 bin = texelFetch(optimumTextures2DArray[OITaccumulation], ivec3(gl_FragCoord.xy, i), 0);
        float anet_k = reveal[i];

        k += vec4(unproject(bin) * anet_k, anet_k) * a;
        a *= 1.0 - anet_k;

    }

    outColor = vec4(unproject(k), anet);
    outGlow = texture(optimumTextures2D[inGlow], v_texcoord);

#if TAAMOTION == 1
    // anet is the fraction of this pixel the transparent layer covers - the
    // very alpha this pass is composited with. It is the reactive value: at 1
    // the pixel is entirely transparent content with no motion vector of its
    // own, at 0 the pixel is untouched by it.
    outMotion = optimumWriteReactiveOnly(clamp(anet, 0.0, 1.0));
#endif

}

#endif
