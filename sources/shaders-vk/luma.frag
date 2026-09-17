#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of luma.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "luma.interface.glsl"

layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;

float luma(vec3 color) {
  return dot(color, vec3(0.299, 0.587, 0.114));
}

void main(void)
{
	vec4 color = texture(optimumTextures2D[scene], texCoord);
	color.a = luma(color.rgb);
	outColor = color;
}
