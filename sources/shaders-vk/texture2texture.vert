#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of texture2texture.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "texture2texture.interface.glsl"

layout(location = 0) in vec3 pos;
layout(location = 1) in vec2 uvIn;

layout(location = 0) out vec2 uv;

void main(void)
{
	uv = uvIn;

	vec2 posTL = (pos.xy  + 1) / 2;

	posTL.x = xs + posTL.x * width;
	posTL.y = ys + posTL.y * height;
	vec2 posOut = posTL * 2 - 1;

	gl_Position = vec4(posOut.x, posOut.y, 0, 1);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
