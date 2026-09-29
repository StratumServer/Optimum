#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of autocamera.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of autocamera.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of autocamera (docs/vulkan.md). No samplers and no scalars: the two
// matrices are the whole record, and there is no push block. The includes' uniforms are frame members
// (autocamera includes shadowcoords.vsh and fogandlight.vsh, their owners).
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;
layout(location = 2) in int renderFlags;

layout(location = 0) out vec4 color;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	vec4 cameraPos = modelViewMatrix * vec4(vertexPositionIn, 1.0);
	color = applyLight(vec3(1), vec4(1), renderFlags, cameraPos) * vertexColor;
	gl_Position = projectionMatrix * cameraPos;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

void main () {
	outColor = color;
    outGlow = vec4(glowLevel, 0, 0, color.a);
}

#endif
