#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of shadowmapentityanimated.vsh (vanilla asset, docs/vulkan-native-shaders.md). No variant axes.
// The Animation block is a storage buffer at set 2 (section 3) with the same member; the bone array is a
// runtime array because MAXANIMATEDELEMENTS is no longer a define.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "shadowmapentityanimated.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

// UBO:Animation,0,4800
layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION) readonly buffer Animation
{
    mat4 values[];
} ElementTransforms;

layout(location = 0) out vec2 uv;

void main(void)
{
	vec4 cameraPos = modelViewMatrix * ElementTransforms.values[jointId] * vec4(vertexPositionIn, 1.0);
	uv = uvIn;
	gl_Position = projectionMatrix * cameraPos;

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
