#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec4 color;
in vec2 uv;
in float glowLevel;
in float fogAmount;
in vec4 rgbaFog;
in vec3 normal;
in vec4 worldPos;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
#if !defined(SSAO_DEPTH_RECONSTRUCT) || SSAO_DEPTH_RECONSTRUCT == 0
in vec4 fragPosition;
layout(location = 3) out vec4 outGPosition;
#endif
in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
#endif

#if SSAOLEVEL > 0 && defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
vec2 optimumOctEncodeNormal(vec3 n) {
	float lenSq = dot(n, n);
	if (lenSq <= 1e-10) {
		return vec2(0.0);
	}
	n *= inversesqrt(lenSq);
	vec2 p = n.xy * (1.0 / (abs(n.x) + abs(n.y) + abs(n.z)));
	if (n.z < 0.0) {
		p = (1.0 - abs(p.yx)) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
	}
	return p;
}
#endif

#include fogandlight.fsh
#include underwatereffects.fsh

void main()
{
	#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	#else
	float intensity = 0.45;
	#endif

	

	float murkiness = getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(color, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);	
	} else {	
		outColor = applyFogAndShadowWithNormal(color, fogAmount, normal, 1, intensity, worldPos.xyz);
	}

	outGlow = vec4(glowLevel, 0, 0, outColor.a);
	//outColor = vec4((normal.x + 1) / 2.0, (normal.y + 1) / 2.0, (normal.z + 1) / 2.0, 1);
	
#if SSAOLEVEL > 0
#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	outGNormal = vec4(optimumOctEncodeNormal(gnormal.xyz), fogAmount + glowLevel, outColor.a);
#else
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, outColor.a);
#endif
#endif
}