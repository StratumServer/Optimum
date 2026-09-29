#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of fsr-rcas.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of fsr-rcas.fsh (docs/vulkan.md).
// AMD FidelityFX Super Resolution 1 RCAS, adapted for a fragment pass.
// FidelityFX FSR 1 source carries the MIT license, AMD 2021.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of fsr-rcas (docs/vulkan.md). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slot and every other uniform is a record member.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, inputScene);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 inputTexelSize;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) out vec2 texCoord;

void main(void)
{
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
	float y = -1.0 + float((gl_VertexIndex & 2) << 1);
	gl_Position = vec4(x, y, 0.0, 1.0);
	texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	vec3 b = texture(optimumTextures2D[inputScene], texCoord + vec2(0.0, -inputTexelSize.y)).rgb;
	vec3 d = texture(optimumTextures2D[inputScene], texCoord + vec2(-inputTexelSize.x, 0.0)).rgb;
	vec3 e = texture(optimumTextures2D[inputScene], texCoord).rgb;
	vec3 f = texture(optimumTextures2D[inputScene], texCoord + vec2(inputTexelSize.x, 0.0)).rgb;
	vec3 h = texture(optimumTextures2D[inputScene], texCoord + vec2(0.0, inputTexelSize.y)).rgb;

	vec3 minimumRing = min(min(b, d), min(f, h));
	vec3 maximumRing = max(max(b, d), max(f, h));
	vec3 hitMinimum = min(minimumRing, e) / max(4.0 * maximumRing, vec3(1.0 / 65536.0));
	vec3 hitMaximumDenominator = min(4.0 * minimumRing - vec3(4.0), vec3(-1.0 / 65536.0));
	vec3 hitMaximum = (vec3(1.0) - max(maximumRing, e)) / hitMaximumDenominator;
	vec3 lobeChannels = max(-hitMinimum, hitMaximum);
	float lobe = max(-0.1875, min(max(max(lobeChannels.r, lobeChannels.g), lobeChannels.b), 0.0));
	lobe *= exp2(-0.2);

	vec3 sharpened = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);
	outColor = vec4(clamp(sharpened, 0.0, 1.0), 1.0);
}

#endif
