#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of ui-compose.fsh (docs/vulkan-native-shaders.md): the UI image passed through
// untouched, so the premultiplied blend stage composes it over the display image.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "ui-compose.interface.glsl"

layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	outColor = texture(optimumTextures2D[uiTex], texCoord);
}
