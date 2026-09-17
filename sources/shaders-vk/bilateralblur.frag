#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of bilateralblur.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "bilateralblur.interface.glsl"

// bilateralblur.fsh's `in vec2 frameSize;` is dropped: see bilateralblur.interface.glsl.
layout(location = 0) in vec2 texCoords[11];

layout(location = 0) out vec4 outColor;

// For info on the bilateral blur see https://www.gamasutra.com/blogs/PeterWester/20140116/208742/Generating_smooth_and_cheap_SSAO_using_Temporal_Blur.php
// http://dev.theomader.com/gaussian-kernel-calculator/
void main(void)
{
	vec4 out_colour = vec4(0.0);

	float refDepth = texture(optimumTextures2D[depthTexture], texCoords[5]).r;
	float fac = 300;


	float w0 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[0]).r - refDepth) * fac, 0, 1)) * 0.003456;
	float w1 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[1]).r - refDepth) * fac, 0, 1)) * 0.015715;
	float w2 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[2]).r - refDepth) * fac, 0, 1)) * 0.051008;
	float w3 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[3]).r - refDepth) * fac, 0, 1)) * 0.118235;
	float w4 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[4]).r - refDepth) * fac, 0, 1)) * 0.195779;
	float w5 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[5]).r - refDepth) * fac, 0, 1)) * 0.231613;
	float w6 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[6]).r - refDepth) * fac, 0, 1)) * 0.195779;
	float w7 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[7]).r - refDepth) * fac, 0, 1)) * 0.118235;
	float w8 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[8]).r - refDepth) * fac, 0, 1)) * 0.051008;
	float w9 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[9]).r - refDepth) * fac, 0, 1)) * 0.015715;
	float w10 = (1 - clamp(abs(texture(optimumTextures2D[depthTexture], texCoords[10]).r - refDepth) * fac, 0, 1)) * 0.003456;

	float wsum = w0 + w1 + w2 + w3 + w4 + w5 + w6 + w7 + w8 + w9 + w10;

	out_colour += texture(optimumTextures2D[inputTexture], texCoords[0]) * w0;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[1]) * w1;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[2]) * w2;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[3]) * w3;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[4]) * w4;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[5]) * w5;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[6]) * w6;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[7]) * w7;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[8]) * w8;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[9]) * w9;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[10]) * w10;

	outColor = out_colour / wsum;
	//outColor = out_colour / 1; // Borderlands mode XD
}
