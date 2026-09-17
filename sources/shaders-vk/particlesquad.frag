#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad.fsh (docs/vulkan-native-shaders.md).
// USEOIT is an axis because oit.fsh gates its outputs on it. The program is registered with Oit = true, so
// only USEOIT=1 is ever selected; the USEOIT=0 variant exists because the builder compiles every axis value,
// and there the OIT call (which has nothing to write to) is compiled out (docs/vulkan-native-shaders.md
// section 9.1).
// The vertex stage includes fogandlight.vsh and vertexwarp.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "particlesquad.interface.glsl"

layout(location = 0) in vec4 color;
layout(location = 5) in vec2 uv;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in vec3 vexPos;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in float extraWeight;



#include "fogandlight.frag.glsl"
#include "underwatereffects.glsl"
#include "oit.glsl"

void main()
{
	vec4 outColor;

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadow(color, 0);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadow(color, fogAmount);
	}

	vec2 uvdist = vec2(
		max(max(0.0, 0.1 - uv.x), max(0.0, uv.x - 0.9)),
		max(max(0.0, 0.1 - uv.y), max(0.0, uv.y - 0.9))
	);

	outColor.a *= 1 - length(uvdist)*10;

#if USEOIT == 1
    OIT(clamp(outColor, vec4(0.0), vec4(1.0)), glowLevel);
#endif

}
