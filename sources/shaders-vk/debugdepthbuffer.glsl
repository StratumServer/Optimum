#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of debugdepthbuffer.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of debugdepthbuffer.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of debugdepthbuffer (docs/vulkan.md). One draw per Use(), so the
// push block holds only the sampler slot and debugdepthbuffer.vsh's two matrices are the record.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthSampler);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;

layout(location = 0) out vec3 vertexPosition;

void main(void)
{
	vertexPosition = vertexPositionIn;

	gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec3 vertexPosition;

layout(location = 0) out vec4 outColor;

float LinearizeDepth(vec2 uv)
{
  float n = 0.3; // camera z near
  float f = 1500.0; // camera z far
  float z = texture(optimumTextures2D[depthSampler], uv).x;
  return z; //(2.0 * n) / (f + n - z * (f - n));
}

void main()
{
	vec2 uv = vertexPosition.xy;

	// Don't ask me why this is nieeded, i guess our quad is weird
	uv.x = uv.x / 2 + 0.5;
	uv.y = uv.y / 2 + 0.5;

	float d;
	d = LinearizeDepth(uv);
	outColor = vec4(d, d, d, 1.0);
}

#endif
