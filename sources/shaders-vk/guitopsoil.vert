#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guitopsoil.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "guitopsoil.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 uv2;
layout(location = 2) out vec4 rgba; // Without biome tint
layout(location = 3) out vec4 rgba2; // With biome tint


void main(void)
{
	uv = uvIn;
    uv2 = uvIn;
    rgba = vec4(1);
    rgba2 = vec4(1);
	float glowLevel = extraGlow / 128.0;

	vec4 color = rgbaIn * (1 + glowLevel);
	if (applyColor == 1) color *= colorIn;

	gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
