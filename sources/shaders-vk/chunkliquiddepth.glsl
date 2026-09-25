#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquiddepth.vsh (vanilla, docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquiddepth.fsh (vanilla, docs/vulkan.md). The GLSL 330 output has no
// location; it is the only one, so it takes location 0.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunkliquiddepth (docs/vulkan.md). One draw per mesh pool:
// the push block holds origin and modelViewMatrix (76 B). The program includes no owner of viewDistance
// (fogandlight.vsh), so it is a record member here, with projectionMatrix and the previous-frame warp state
// vertexwarp.glsl reads.
layout(push_constant, scalar) uniform OptimumDraw
{
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    float viewDistance;

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


layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlags;
layout(location = 4) in vec2 flowVector;
layout(location = 5) in int colormapData;
layout(location = 6) in int waterFlagsIn;

#include "vertexflagbits.glsl"
#include "vertexwarp.glsl"


void main(void)
{
	vec4 worldPos = vec4(xyz + origin, 1.0);

	float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) ? 90 : 5;

	float oceanity = ((waterFlagsIn >> 2) & 0xff) / 255.0;
	div *= max(0.2, 1 - oceanity);

	if ((waterFlagsIn & 1) == 1) {
		worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div);
	}

	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

	// Distance fade out
	float a = length(worldPos.xz) / viewDistance;
	gl_Position.w -= max(0.0, (a-0.75)*5);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) out vec4 outColor;

void main()
{
	outColor=vec4(1);
}

#endif
