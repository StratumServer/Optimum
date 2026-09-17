#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of colorgrade.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "colorgrade.interface.glsl"

layout(location=0) in vec2 position;

layout(location = 0) out vec2 texCoord;
layout(location = 1) out vec2 invFrameSize;

void main(void)
{
    gl_Position = vec4(position, 0, 1);
    texCoord = (position+1.0) / 2.0;
	invFrameSize = invFrameSizeIn;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
