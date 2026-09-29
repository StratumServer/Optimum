#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of woittest.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of woittest.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of woittest (docs/vulkan.md). No samplers, so no push block;
// woittest.vsh's two matrices are the record.
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec4 colorIn;

layout(location = 0) out vec4 v_color;
//out float depth;

void main () {
	v_color = colorIn;
	gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPosition, 1.0);

	//depth = -(modelViewMatrix * vec4(vertexPosition, 1.0)).z;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec4 v_color;

layout(location = 0) out vec4 outAccu;
layout(location = 1) out vec4 outReveal;


void drawPixel(vec4 color) {
    float alpha = color.a;

	float weight = max(0.01, min(3000, 0.03 / (0.00001 + pow(gl_FragCoord.z/200, 4))));

    // RGBA32F texture (accumulation)
    outAccu = vec4(color.rgb * alpha, alpha) * weight;

    // R32F texture (revealage)
    // Make sure to use the red channel (and GL_RED target in your texture)
    outReveal.r = alpha;
}

void main()
{
	drawPixel(v_color);
}

#endif
