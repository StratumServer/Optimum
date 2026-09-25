#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of wireframe.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of wireframe.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of wireframe (docs/vulkan.md). No samplers, so there is no push block:
// the record holds wireframe.vsh's uniforms in declaration order, then vertexwarp.vsh's prev* uniforms in its
// header's order (their owner rule: they are program uniforms, not frame members). The prev* GLSL 330
// initializers (some are 1) are seeded by the runtime (section 8).
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    vec4 colorIn;
    vec3 origin;
    float extraGlow;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;
layout(location = 2) in int renderFlags;

layout(location = 0) out vec4 color;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	vec4 worldPos = applyVertexWarping(renderFlags, vec4(vertexPositionIn + origin, 1.0));
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;

	color = max(vertexColor, vec4(0.001, 0.001, 0.001, 0));


	glowLevel = extraGlow;
	color = applyLightWithoutPointLight(color, color,  0);
	color.a = vertexColor.a;
	gl_Position = projectionMatrix * cameraPos;
	color *= colorIn;

	// Pretend the vertices are closer to the camera to enforce it always being drawn on top
	gl_Position.w += 0.0014 + (renderFlags >> 8) * 0.00025;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

void main () {
  outColor = color;
  outGlow = vec4(glowLevel, 0, 0, color.a);
}

#endif
