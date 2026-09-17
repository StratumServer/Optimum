#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquiddepth.vsh (vanilla, docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunkliquiddepth.interface.glsl"

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
