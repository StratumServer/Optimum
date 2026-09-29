#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of texture2texture.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of texture2texture.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of texture2texture (docs/vulkan.md). One draw per Use(), so the push
// block holds only the sampler slot and every other uniform is a record member: texture2texture.vsh's, then
// texture2texture.fsh's, each in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float xs;
    float ys;
    float width;
    float height;

    float texu;
    float texv;
    float texw;
    float texh;
    float alphaTest;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 pos;
layout(location = 1) in vec2 uvIn;

layout(location = 0) out vec2 uv;

void main(void)
{
	uv = uvIn;

	vec2 posTL = (pos.xy  + 1) / 2;

	posTL.x = xs + posTL.x * width;
	posTL.y = ys + posTL.y * height;
	vec2 posOut = posTL * 2 - 1;

	gl_Position = vec4(posOut.x, posOut.y, 0, 1);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;

layout(location = 0) out vec4 outColor;


void main () {
	outColor = texture(optimumTextures2D[tex2d], vec2(texu + uv.x * texw, texv + uv.y * texh));
	if (outColor.a <= alphaTest) discard;
}

#endif
