#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec3 vertexPosition;
in vec4 rgbaFog;
in float nightVisionStrengthv;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float dayLight;
uniform float horizonFog;
uniform vec3 playerPos;
uniform vec3 sunPosition;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
#if !defined(SSAO_DEPTH_RECONSTRUCT) || SSAO_DEPTH_RECONSTRUCT == 0
layout(location = 3) out vec4 outGPosition;
#endif
#endif

#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

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
	
#if SSAOLEVEL > 0
#if !defined(SSAO_DEPTH_RECONSTRUCT) || SSAO_DEPTH_RECONSTRUCT == 0
	outGPosition = vec4(0);
#endif
	outGNormal = vec4(0);
#endif

}
