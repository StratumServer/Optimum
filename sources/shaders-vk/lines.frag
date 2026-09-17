#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of lines.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "lines.interface.glsl"

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;


void main() {
	outColor = color;
	outGlow = vec4(glowLevel, 0, 0, outColor.a);
}
