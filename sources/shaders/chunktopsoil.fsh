#version 330 core

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

uniform float alphaTest = 0.01;
uniform vec2 blockTextureSize;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in vec2 uv2;
in float glowLevel;
in vec3 blockLight;
in vec4 worldPos;
in vec3 vertexPosition;

flat in int renderFlags;
in vec3 normal;
in vec4 gnormal;



layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
#if !defined(SSAO_DEPTH_RECONSTRUCT) || SSAO_DEPTH_RECONSTRUCT == 0
in vec4 fragPosition;
layout(location = 3) out vec4 outGPosition;
#endif
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

#include vertexflagbits.ash
#include fogandlight.fsh
#include colormap.fsh
#include noise3d.ash
#include underwatereffects.fsh

void main() 
{
	vec4 brownSoilColor = texture(terrainTex, uv) * rgba;
	
      	if (normal.y >= 0) {
      		 // Top (normal.y == 1) or Sides (normal.y == 0)
      		vec4 grassColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))) * rgba;
      		outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
      	} else {
      		 // Bottom
      		outColor = applyFog(brownSoilColor, fogAmount);
	}
	
	if (psychedelicStrength > Epsilon) outColor = applyPsychedelicEffect(outColor, vertexPosition*2, 0);
	if (glitchStrength > Epsilon) outColor = applyRustEffect(outColor, normal, vertexPosition, 1);
	

	#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	#else
	float intensity = 0.45;
	#endif
	
	
	
	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowWithNormal(outColor, clamp(fogAmount - 50*murkiness, 0, 1), normal, 1, intensity, worldPos.xyz);
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);		
	
	outColor.a = rgbaFog.a;

	float aTest = outColor.a;
	aTest += max(0.0, 1 - rgba.a) * min(1, outColor.a * 10);
#if NORMALVIEW == 0	
	 // Fade to sky color
         // Also, when looking through tinted glass you can clearly see the edges where we fade to sky color; using the outColor.a < 0.005 discard seems to completely fix that
	if (aTest < alphaTest || outColor.a < 0.005) discard;
#endif


	float glow = 0;

#if SHINYEFFECT > 0
	if ((renderFlags & ReflectiveBitMask) > 0) {
		vec3 worldVec = normalize(worldPos.xyz);
	
		float angle = 2 * dot(normalize(normal), worldVec);
		angle += gnoise(vec3(uv.x*500, uv.y*500, worldVec.z/10)) / 7.5;		
		outColor.rgb *= max(vec3(1), vec3(1) + 3*blockLight * gnoise(vec3(worldVec.x/10 + angle, worldVec.y/10 + angle, worldVec.z/10 + angle)));
	}
	
	glow = pow(max(0.0, dot(normal, lightPosition)), 6) * 0.1 * shadowIntensity * (1 - fogAmount);
#endif	

	

#if SSAOLEVEL > 0
#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	outGNormal = vec4(optimumOctEncodeNormal(gnormal.xyz), fogAmount * 2 + glowLevel, gnormal.w);
#else
	outGPosition = vec4(fragPosition.xyz, fogAmount * 2 + glowLevel);
	outGNormal = gnormal;
#endif
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif

    outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);
}