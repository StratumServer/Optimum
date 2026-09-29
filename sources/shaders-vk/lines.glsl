#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of lines.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of lines.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of lines (docs/vulkan.md). No samplers, and the two matrices alone
// exceed the push budget, so there is no push block: every uniform is a record member, lines.vsh's then
// lines.fsh's, each in declaration order. glowLevel's GLSL 330 initializer (1.0) is seeded by the runtime.
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float lineWidth;
    mat4 projection;
    mat4 view;
    vec3 origin;

    vec4 color;
    float glowLevel;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 quadCoord;  // Per vertex
layout(location = 1) in vec2 uvIn;			// Per vertex

layout(location = 2) in vec3 pointA;
layout(location = 3) in vec3 pointB;

void main() {
	vec3 dir = pointB - pointA;
	vec3 q = quadCoord * vec3(lineWidth, 1, lineWidth) - vec3(lineWidth/2, 0, lineWidth/2);
	float up = q.y;
	gl_Position = projection * view * vec4(pointA + origin + vec3(1, dir.y, 1) * q + vec3(dir.x*up, 0, dir.z*up), 1);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;


void main() {
	outColor = color;
	outGlow = vec4(glowLevel, 0, 0, outColor.a);
}

#endif
