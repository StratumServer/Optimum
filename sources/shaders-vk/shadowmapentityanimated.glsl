#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of shadowmapentityanimated.vsh (vanilla asset, docs/vulkan.md). No variant axes.
// The Animation block is a storage buffer at set 2 (section 3) with the same member; the bone array is a
// runtime array because MAXANIMATEDELEMENTS is no longer a define.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of shadowmapentityanimated.fsh (vanilla asset, docs/vulkan.md). No variant axes.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of shadowmapentityanimated (docs/vulkan.md). The shadow pass
// sets modelViewMatrix per entity (EntityShapeRenderer's isShadowPass branch) and addRenderFlags; with the
// sampler slot they fit the push block (72 B). projectionMatrix is set once per shadow map and goes to the
// record.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, entityTex);

    mat4 modelViewMatrix;
    int addRenderFlags;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
};

#if defined(OPTIMUM_VERTEX)


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

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;

layout(location = 0) out vec4 outColor;

void main () {
	outColor = vec4(texture(optimumTextures2D[entityTex], uv));
	if (outColor.a < 0.01) discard;
}

#endif
