#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blur.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "blur.interface.glsl"

// blur.fsh's `in vec2 frameSize;` is dropped: see blur.interface.glsl.
layout(location = 0) in vec2 texCoords[21];

layout(location = 0) out vec4 outColor;

// http://dev.theomader.com/gaussian-kernel-calculator/
void main(void)
{
	vec4 out_colour = vec4(0.0);
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[0]) * 0.001422;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[1]) * 0.004255;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[2]) * 0.011001;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[3]) * 0.024574;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[4]) * 0.047431;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[5]) * 0.0791;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[6]) * 0.113978;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[7]) * 0.141908;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[8]) * 0.152663;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[9]) * 0.141908;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[10]) * 0.113978;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[11]) * 0.0791;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[12]) * 0.047431;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[13]) * 0.024574;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[14]) * 0.011001;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[15]) * 0.004255;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[16]) * 0.001422;

	outColor = out_colour;
}
