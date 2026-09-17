#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of sky.fsh (docs/vulkan-native-shaders.md). Axis: GBUFFER (the G-buffer outputs).
// The vertex stage includes fogandlight.vsh, which owns the flatFogDensity and fogSpheres that
// fogandlight.fsh and skycolor.fsh read here (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "sky.interface.glsl"

layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in float nightVisionStrengthv;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include "dither.glsl"
#include "fogandlight.frag.glsl"
#include "skycolor.glsl"
#include "underwatereffects.glsl"

void main()
{
	outColor = vec4(1);
	outGlow = vec4(1);
	float sealevelOffsetFactor = 0.25;
	getSkyColorAt(vertexPosition, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, outColor, outGlow);

	if (psychedelicStrength > Epsilon) outColor = applyPsychedelicEffect(outColor, vertexPosition.xyz/2, 0);

	float murkiness = max(0.0, getSkyMurkiness() - 14*fogDensityIn);
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

	outColor.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;
	outGlow.y *= clamp((dayLight - 0.05) * 2 - 50*murkiness, 0, 1);

#if GBUFFER == 1
	outGPosition = vec4(0);
	outGNormal = vec4(0);
#endif

}
