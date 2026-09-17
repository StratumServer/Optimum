#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of woittest.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "woittest.interface.glsl"

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
