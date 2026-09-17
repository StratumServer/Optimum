#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of gui.vsh (docs/vulkan-native-shaders.md).
// The Animation UBO is the set 2 animation storage buffer. MAXANIMATEDELEMENTS is a client setting the
// offline build cannot know, so the array is unsized; jointId indexes it exactly as before.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "gui.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
// Bits 0-7: Glow level
// Bits 8-10: Z-Offset
// Bit 11: Wind waving yes/no
// Bit 12: Water waving yes/no
// Bit 13: low contrast mode
// Bit 14-26: x/y/z normals, 12 bits total. Each axis with 1 sign bit and 3 value bits
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION) readonly buffer Animation
{
    mat4 values[];
} ElementTransforms;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 uvOverlay;
layout(location = 2) out vec4 color;
layout(location = 3) out vec4 rgbaGlow;
layout(location = 4) out vec2 clipPos;
layout(location = 5) out float damageEffectV;

layout(location = 6) flat out vec3 normal;
layout(location = 7) out float normalShadeIntensity;

#include "vertexflagbits.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	damageEffectV = damageEffectIn;
	uv = uvIn;

	int glow = min(255, extraGlow + (renderFlagsIn & GlowLevelBitMask));

	glowLevel = glow / 255.0;
	rgbaGlow = rgbaGlowIn;

	color = rgbaIn;

	if (applyColor == 1) color *= colorIn;

	if (applyAnimation > 0) {
		mat4 animModelMat = modelViewMatrix * ElementTransforms.values[jointId];
		gl_Position = projectionMatrix * animModelMat * vec4(vertexPositionIn, 1.0);
	} else {
		gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);
	}

	clipPos = gl_Position.xy;

	normal = unpackNormal(renderFlagsIn);
	if (applyModelMat > 0) {
		normal = (modelMatrix * vec4(normal, 0)).xyz;
		normal = normalize(normal);
	}

	normalShadeIntensity = 1;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
