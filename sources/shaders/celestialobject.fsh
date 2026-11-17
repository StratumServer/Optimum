#version 330 core

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float glowLevel;
in vec3 vertexPosition;

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


uniform sampler2D tex;
uniform float extraGodray = 0;
uniform float alphaTest = 0.001;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 moonPosition;
uniform float moonSunAngle;
uniform int weirdMathToMakeMoonLookNicer;
uniform float dayLight;

#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

void main () {
	vec4 texColor = applyFog(texture(tex, uv) * color, 0);

	if (texColor.a < alphaTest) discard;

	// Now apply moon phase / sun lighting
	float msa = int(moonSunAngle * 0.31830989 + 0.5) * 3.1415927;    // the moonSunAngle rounded to either 0 or Math.Pi, no angles in between
	float dotp = dot(sunPosition, moonPosition);
	vec3 dirsun = normalize(sunPosition - moonPosition * dotp);
	vec2 xyi = (ivec2(uv * 32) - 16) / 32.0;
	float fact = (clamp((xyi.x * cos(msa) - xyi.y * sin(msa) * sign(dirsun.y)), -1, 1) + 1);
	texColor.rgb *= clamp(7.1 - fact * 6.2 - dotp * 1.7, 0.0, texColor.a + 0.05);

	vec4 texGlow = vec4(glowLevel, extraGodray, 0, texColor.a);
	
	vec4 skyColor = vec4(1);
	vec4 skyGlow = vec4(1);
	float sealevelOffsetFactor = 0.06;
	
	getSkyColorAt(vertexPosition, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);
	
	vec3 skyPosNorm = normalize(vertexPosition.xyz);
	float fogAmount =getFogAmountForSky(vertexPosition, skyPosNorm, sealevelOffsetFactor, horizonFog);
	texColor = applyFog(texColor, fogAmount);

	outColor = texColor;
	outColor.a = texColor.a;


	if (weirdMathToMakeMoonLookNicer > 0) {
		float b = pow((outColor.r+outColor.g+outColor.b) / 2.8, 1.4);
		outColor.rgb *= b;

		float v = max(0.0, (1-texColor.a) * min(1, texColor.a*20)/3 - fogAmount * 0.5);
		outGlow = vec4(v, v, 0, max(0.0, 4-7*dayLight));
		
		outColor.rgb = max(outColor.rgb, skyColor.rgb);
	} else {
		
		outGlow = max(texGlow, skyGlow);
		
		outColor.rgb = max(texColor.rgb, skyColor.rgb);
	}

	float murkiness=getSkyMurkiness();
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if SSAOLEVEL > 0
#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	outGNormal = vec4(optimumOctEncodeNormal(gnormal.xyz), fogAmount + glowLevel, 0);
#else
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, 0);
#endif
#endif

}
