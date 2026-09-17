#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blockhighlights.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "blockhighlights.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;


layout(location = 0) out vec4 color;
layout(location = 1) out vec4 rgbaFog;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	vec4 cameraPos = modelViewMatrix * vec4(vertexPositionIn, 1.0);

	color = vertexColor;
	gl_Position = projectionMatrix * cameraPos;

	// We are cheap. We pretend the highlights are closer to the camera to enforce it
	// always being drawn on top
	gl_Position.w += 0.0004;

	rgbaFog = vec4(0);
	glowLevel = 0;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
