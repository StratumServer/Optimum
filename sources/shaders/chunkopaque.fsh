#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 vertexPosition;
in vec3 blockLight;
in vec4 gnormal;
in vec4 camPos;
in float lod0Fade;
in float nb;

// Greedy mesh tile repeat (Optimum). Compiled in only when the feature
// is on (GREEDYMESH stamped from OptimumConfig at shader load); at 0
// this whole shader preprocesses to vanilla.
#if GREEDYMESH > 0
flat in int tileWidth;
flat in int tileHeight;
flat in vec2 tileBoundsMin;
flat in vec2 tileBoundsSize;
#endif

uniform float alphaTest;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform float dayLight;
uniform int haxyFade;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include vertexflagbits.ash
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include colormap.fsh
#include underwatereffects.fsh

void main() 
{
#if GREEDYMESH > 0
	vec2 sampledUv = uv;

	// Greedy mesh UV tiling (Optimum): when tile counts > 1, the UV
	// interpolated from vertex data covers one tile stretched over N
	// blocks. To repeat the texture, normalize uv into [0,1] within the
	// tile rect, scale by tile count, fract to wrap, and map back to
	// atlas coordinates. This works regardless of axis inversion because
	// it operates purely in UV interpolation space.
	if (tileWidth > 1 || tileHeight > 1) {
		// Normalize uv to [0,1] within the tile sub-rect.
		vec2 normalizedUV = (uv - tileBoundsMin) / tileBoundsSize;
		// Scale by tile count and wrap.
		vec2 tileCount = vec2(float(tileWidth), float(tileHeight));
		vec2 repeatedUV = fract(normalizedUV * tileCount);
		// Map back to atlas coordinates.
		sampledUv = tileBoundsMin + tileBoundsSize * repeatedUV;

#if GREEDYMESH_GRAD > 0
		// Use textureGrad to avoid mipmap seams at fract() boundaries.
		// Derivatives come from the unwrapped uv (smooth across the quad).
		vec2 dx = dFdx(uv) * tileCount;
		vec2 dy = dFdy(uv) * tileCount;
		vec4 texColor = getColorMapped(terrainTexLinear, textureGrad(terrainTex, sampledUv, dx, dy)) * rgba;
#else
		// A/B path (GreedyMeshTextureGrad false): plain sampler, implicit
		// derivatives spike at the fract() wrap so distant merged quads can
		// show mip seams. Trades that artifact for skipping the explicit-
		// gradient sampler, which runs at reduced rate on some GPUs.
		vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, sampledUv)) * rgba;
#endif

		if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition*2, 0);
		if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition, 1);

		float b = getBrightnessFromShadowMap();
		float murkiness = getUnderwaterMurkiness();
		outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz);

		float glow = 0;
		float godrayLevel = 0;

		if (haxyFade > 0) {
			if (rgba.a < 0.999) {
				vec4 skyColor = vec4(1);
				vec4 skyGlow = vec4(1);
				float sealevelOffsetFactor = 0.25;
				getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);
				godrayLevel = skyGlow.g;
				outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1-dayLight, max(0.0, rgba.a)));
			}
		}

		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if NORMALVIEW == 0
		float aTest = outColor.a + max(0.0, 1 - rgba.a) * min(1, outColor.a * 10) - lod0Fade;
		if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif

#if SHINYEFFECT > 0
		if ((renderFlags & ReflectiveBitMask) != 0) {
			outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, sampledUv, normal, worldPos, camPos, blockLight), outColor, clamp(2 * fogAmount + 2*(1-b), 0, 1));
		}
		glow += pow(max(0.0, dot(normal, lightPosition)), 6) * 0.125 * shadowIntensity * (1 - fogAmount - murkiness);
#endif

#if SSAOLEVEL > 0
		outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
		outGNormal = gnormal;
#endif

#if NORMALVIEW > 0
		outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
#endif
		outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
		return;
	}
#endif

	// --- Vanilla path (no tiling) ---
	vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv)) * rgba;
	
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition*2, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition, 1);
	
	float b = getBrightnessFromShadowMap();
	
	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz); 
	
	float glow = 0;
	float godrayLevel = 0;

	if (haxyFade > 0) {
	    if (rgba.a < 0.999) {
			vec4 skyColor = vec4(1);
			vec4 skyGlow = vec4(1);
			float sealevelOffsetFactor = 0.25;
		
			getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);
			godrayLevel = skyGlow.g;
			outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1-dayLight, max(0.0, rgba.a)));
	    }
	}

	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);


#if NORMALVIEW == 0	
	float aTest = outColor.a + max(0.0, 1 - rgba.a) * min(1, outColor.a * 10) - lod0Fade;
	
	if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;
	
	if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif


#if SHINYEFFECT > 0
	if ((renderFlags & ReflectiveBitMask) != 0) {
		outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2 * fogAmount + 2*(1-b), 0, 1));
	}
	glow += pow(max(0.0, dot(normal, lightPosition)), 6) * 0.125 * shadowIntensity * (1 - fogAmount - murkiness);
#endif


#if SSAOLEVEL > 0
	outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
	outGNormal = gnormal;
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif
	
	outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
}
