#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of shadowmapentityanimated.fsh (vanilla asset, docs/vulkan-native-shaders.md). No variant axes.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "shadowmapentityanimated.interface.glsl"

layout(location = 0) in vec2 uv;

layout(location = 0) out vec4 outColor;

void main () {
	outColor = vec4(texture(optimumTextures2D[entityTex], uv));
	if (outColor.a < 0.01) discard;
}
