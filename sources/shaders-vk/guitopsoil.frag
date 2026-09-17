#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guitopsoil.fsh (docs/vulkan-native-shaders.md).
// color and glowLevel are declared by the GLSL 330 stage but written by no vertex stage and read by nothing;
// they keep their declarations (glowLevel at its shared varying location), and the optimised module drops them.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "guitopsoil.interface.glsl"
#include "varyings.glsl"

layout(location = 2) in vec4 rgba; // Without biome tint
layout(location = 3) in vec4 rgba2; // With biome tint
layout(location = 0) in vec2 uv;
layout(location = 1) in vec2 uv2;
layout(location = 4) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;

layout(location = 0) out vec4 outColor;

void main () {

	vec4 brownSoilColor = texture(optimumTextures2D[terrainTex], uv) * rgba;
	vec4 grassColor;

	if (rgba.a < 0.01) {
		// Bottom
		outColor = brownSoilColor;
	} else {
		if (rgba2.a > 0.01) {
			// Top
			grassColor = texture(optimumTextures2D[terrainTex], uv2) * rgba2;
		} else {
			// Side + Overlay
			grassColor = texture(optimumTextures2D[terrainTex], uv2 + vec2(blockTextureSize, 0)) * vec4(rgba2.rgb, 1);
		}

		outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
	}
	outColor.a = 1;
}
