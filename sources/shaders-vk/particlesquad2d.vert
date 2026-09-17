#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad2d.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "particlesquad2d.interface.glsl"

layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec2 uvIn;					// Per vertex
layout (location = 2) in vec4 baseColor;			// Per vertex

layout (location = 3) in vec4 inColor;				// Per instance
layout (location = 4) in vec3 particlePosition; 	// Per instance
layout (location = 5) in float scale;					// Per instance
layout (location = 6) in float inGlow;				// Per instance

layout(location = 0) out vec4 color;
layout(location = 1) out vec2 uv;
layout(location = 2) out float glowLevel;

void main()
{
	color = baseColor * inColor;
	uv = uvIn;
	glowLevel = inGlow;

	vec3 pos = vec3(vertexPosition.x * scale, vertexPosition.y * scale, vertexPosition.z);

	vec4 cameraPos = modelViewMatrix * vec4(pos + particlePosition, 1.0);
	gl_Position = projectionMatrix * cameraPos;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
