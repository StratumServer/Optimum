#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquiddepth.fsh (vanilla, docs/vulkan-native-shaders.md). The GLSL 330 output has no
// location; it is the only one, so it takes location 0.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunkliquiddepth.interface.glsl"

layout(location = 0) out vec4 outColor;

void main()
{
	outColor=vec4(1);
}
