#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of aurora.fsh (docs/vulkan-native-shaders.md).
// USEOIT is an axis because oit.fsh gates its outputs on it; the program is registered with Oit = true, and
// in the never-selected USEOIT=0 variant the writes to oit.fsh's outputs are compiled out (family 5 decision).
// The vertex stage includes fogandlight.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "aurora.interface.glsl"

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 col;
layout(location = 2) in vec4 rgbaFog;
layout(location = 4) in vec4 vexPos;
layout(location = 5) in float xpos;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 6) flat in int renderFlags;



#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "oit.glsl"

void main () {
	vec4 outColor = vec4(1);

	outColor = applyFogAndShadow(outColor, fogAmount);

	if (outColor.a < alphaTest) discard;

	float rndr = min(0.6, cnoise(vec3(vexPos.x/500.0, -vexPos.z/600.0, auroraCounter))*2.3 - 1);
	float rndg = cnoise(vec3(vexPos.x/600.0, vexPos.z/600.0, 1 - auroraCounter))/3;
	float rndb = cnoise(vec3(vexPos.x/600.0, vexPos.z/600.0, 1 - auroraCounter))/5;

	outColor = vec4(
		clamp(rndr, 0, 1),
		clamp(0.5 + rndg - rndr, 0, 0.8),
		clamp(0.5 + rndb, 0, 1),
		max(0, 0.7 - uv.y * 0.7)/2
	);

	float advx = xpos * 10; // was uv.x

	outColor.a *=
		1
		+ 0.7 * cnoise(vec3(advx/6 + (vexPos.x)/120.0, uv.y/2 + (vexPos.z)/120.0, auroraCounter))
		+ 0.5 * cnoise(vec3(advx/3 + (vexPos.x)/60.0, uv.y/1.5 + (vexPos.z)/60.0, auroraCounter))
		+ 0.2 * cnoise(vec3(advx/1.5 + (vexPos.x)/30.0, uv.y + (vexPos.z)/30.0, auroraCounter))
	;


	//outColor.a *= 0.7;

	outColor.a *= clamp(9*uv.y - 1, 0, 1);

	//outColor.a=1;


	outColor.a = clamp(outColor.a, 0, 1);

	outColor *= col;

	// Fade edges
	// http://fooplot.com/#W3sidHlwZSI6MCwiZXEiOiJtaW4oMSxtaW4oMTAqeCwoMS14KSoxMCkpIiwiY29sb3IiOiIjMDAwMDAwIn0seyJ0eXBlIjoxMDAwLCJ3aW5kb3ciOlsiMCIsIjEiLCIwIiwiMiJdLCJzaXplIjpbNjQ5LDM5OV19XQ--
	float a = min((xpos - 0.1) * 20, (1 - xpos) * 20);
	outColor.a *= min(1, a);

#if USEOIT == 1
	OIT(clamp(outColor, vec4(0.0), vec4(1.0)), 0.0);
	outGlow = vec4(1, extraGodray, 0, min(1, outColor.a * 6));
#endif

}
