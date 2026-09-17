#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of wireframe.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "wireframe.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;
layout(location = 2) in int renderFlags;

layout(location = 0) out vec4 color;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	vec4 worldPos = applyVertexWarping(renderFlags, vec4(vertexPositionIn + origin, 1.0));
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;

	color = max(vertexColor, vec4(0.001, 0.001, 0.001, 0));


	glowLevel = extraGlow;
	color = applyLightWithoutPointLight(color, color,  0);
	color.a = vertexColor.a;
	gl_Position = projectionMatrix * cameraPos;
	color *= colorIn;

	// Pretend the vertices are closer to the camera to enforce it always being drawn on top
	gl_Position.w += 0.0014 + (renderFlags >> 8) * 0.00025;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
