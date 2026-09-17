#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blit.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "blit.interface.glsl"

layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;


void main(void)
{
	outColor = texture(optimumTextures2D[scene], texCoord);
	outColor.a = 1;
}
