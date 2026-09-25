#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of findbright.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of findbright.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of findbright (docs/vulkan.md). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots and every other uniform is a record member.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, colorTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, glowTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float extraBloom;
    float ambientBloomLevel;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) out vec2 texcoord;

void main(void)
{
	// https://rauwendaal.net/2014/06/14/rendering-a-screen-covering-triangle-in-opengl/
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    texcoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 texcoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	vec4 color = texture(optimumTextures2D[colorTex], texcoord);
	float glowLevel = texture(optimumTextures2D[glowTex], texcoord).r * color.a;
	float bloomIntensity = ambientBloomLevel + 3*glowLevel + extraBloom;

	outColor = color * bloomIntensity;
	//outColor = color * 2;  - night vision
}

#endif
