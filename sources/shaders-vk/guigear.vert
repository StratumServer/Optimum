#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guigear.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "guigear.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;


layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 pos;

void main(void)
{
	gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);

	uv = uvIn;
	pos = (modelViewMatrix * vec4(vertexPositionIn, 1.0)).xy;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
