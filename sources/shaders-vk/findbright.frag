#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of findbright.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "findbright.interface.glsl"

layout(location = 0) in vec2 texcoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	vec4 color = texture(optimumTextures2D[colorTex], texcoord);
	float glowLevel = texture(optimumTextures2D[glowTex], texcoord).r * color.a;
	float bloomIntensity = ambientBloomLevel + 3*glowLevel + extraBloom;

	outColor = color * bloomIntensity;
	//outColor = color * 2;  - night vision
}
