#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of texture2texture.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "texture2texture.interface.glsl"

layout(location = 0) in vec2 uv;

layout(location = 0) out vec4 outColor;


void main () {
	outColor = texture(optimumTextures2D[tex2d], vec2(texu + uv.x * texw, texv + uv.y * texh));
	if (outColor.a <= alphaTest) discard;
}
