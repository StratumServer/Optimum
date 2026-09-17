#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of lines.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "lines.interface.glsl"

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
