#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkshadowmap.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunkshadowmap.interface.glsl"

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main () {
	outColor = texture(optimumTextures2D[tex2d], uv);
	// Optimum: raise discard threshold from 0.02 to 0.15.
	// Skips more near-transparent shadow fragments (grass edges, leaf fringes)
	// with no visible shadow quality loss at typical view distances.
	if (outColor.a < 0.15) discard;

}
