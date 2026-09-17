#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkopaque.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: GREEDYMESH (tile varyings), GBUFFER (G-buffer outputs; the motion output moves from 2 to 4 with it),
// TAAMOTION (motion output, written through include/motion.glsl). GREEDYMESH_GRAD, NORMALVIEW and
// SHINYEFFECT are specialization-constant branches.
//
// fogandlight.frag.glsl and fogspheres.glsl read names owned by fogandlight.vsh and vertexwarp.vsh, which the
// vertex stage includes, so this stage activates those owners' names itself (contract section 3).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunkopaque.interface.glsl"
#include "varyings.glsl"

layout(location = 0) in vec4 rgba;
layout(location = 2) in vec4 rgbaFog;
layout(location = 3) in float fogAmount;
layout(location = 1) in vec2 uv;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 9) flat in int renderFlags;
layout(location = 4) in vec3 normal;
layout(location = 6) in vec4 worldPos;
layout(location = 5) in vec3 vertexPosition;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
#if GBUFFER == 1
layout(location = 14) in vec4 gnormal;
#endif
layout(location = 7) in vec4 camPos;
layout(location = 8, component = 0) in float lod0Fade;
layout(location = 8, component = 1) in float nb;

// Greedy mesh tile repeat (Optimum). Compiled in only when the feature
// is on (GREEDYMESH stamped from OptimumConfig at shader load); at 0
// this whole shader preprocesses to vanilla.
#if GREEDYMESH > 0
layout(location = 10) flat in int tileWidth;
layout(location = 11) flat in int tileHeight;
layout(location = 12) flat in vec2 tileBoundsMin;
layout(location = 13) flat in vec2 tileBoundsSize;
#endif

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

// TAA motion vectors (Optimum P3). The location is the Primary colour attachment the motion texture
// occupies (TAAMOTIONLOCATION: 2 without the SSAO G-buffer, 4 with it). The vector, reactive and writer
// depth come from include/motion.glsl (contract section 7).
#if TAAMOTION > 0
layout(location = 15) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "dither.glsl"
#include "skycolor.glsl"
#include "colormap.frag.glsl"
#include "underwatereffects.glsl"

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

		// Declared before the branch that assigns it (contract section 5); both paths overwrite it.
		vec4 texColor = vec4(0.0);
		if (OPTIMUM_GREEDYMESH_GRAD > 0) {
		// Use textureGrad to avoid mipmap seams at fract() boundaries.
		// Derivatives come from the unwrapped uv (smooth across the quad).
		vec2 dx = dFdx(uv) * tileCount;
		vec2 dy = dFdy(uv) * tileCount;
		texColor = getColorMapped(optimumTextures2D[terrainTexLinear], textureGrad(optimumTextures2D[terrainTex], sampledUv, dx, dy)) * rgba;
		} else {
		// A/B path (GreedyMeshTextureGrad false): plain sampler, implicit
		// derivatives spike at the fract() wrap so distant merged quads can
		// show mip seams. Trades that artifact for skipping the explicit-
		// gradient sampler, which runs at reduced rate on some GPUs.
		texColor = getColorMapped(optimumTextures2D[terrainTexLinear], texture(optimumTextures2D[terrainTex], sampledUv)) * rgba;
		}

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

		if (OPTIMUM_NORMALVIEW == 0) {
		float aTest = outColor.a + max(0.0, 1 - rgba.a) * min(1, outColor.a * 10) - lod0Fade;
		if (aTest < alphaTest || rgba.a < 0.005) discard;
		}

		if (OPTIMUM_SHINYEFFECT > 0) {
		if ((renderFlags & ReflectiveBitMask) != 0) {
			outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, sampledUv, normal, worldPos, camPos, blockLight), outColor, clamp(2 * fogAmount + 2*(1-b), 0, 1));
		}
		glow += pow(max(0.0, dot(normal, lightPosition)), 6) * 0.125 * shadowIntensity * (1 - fogAmount - murkiness);
		}

#if GBUFFER == 1
		outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
		outGNormal = gnormal;
		if (OPTIMUM_OPTIMUMAO > 0) {
		// Optimum AO class channel (docs/research/ambient-occlusion.md C.5): the thin class is the
		// vertex stage's wind flag, which vanilla already writes into gnormal.w (grass, plants and
		// leaves wave; blocks and snow layers do not). It used to be forced to 1 for the whole
		// blend-no-cull pool as well, but that pool holds solid blocks too - snow layers - so in a
		// snow-covered world 59 % of the visible pixels were 0.05-block occluders (2026-09-17).
		}
#endif

		if (OPTIMUM_NORMALVIEW > 0) {
		outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
		}
		outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
#if TAAMOTION > 0
		// Opaque terrain is not reactive.
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, 0.0, gl_FragCoord.z);
#endif
		return;
	}
#endif

	// --- Vanilla path (no tiling) ---
	vec4 texColor = getColorMapped(optimumTextures2D[terrainTexLinear], texture(optimumTextures2D[terrainTex], uv)) * rgba;

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


	if (OPTIMUM_NORMALVIEW == 0) {
	float aTest = outColor.a + max(0.0, 1 - rgba.a) * min(1, outColor.a * 10) - lod0Fade;

	if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;

	if (aTest < alphaTest || rgba.a < 0.005) discard;
	}


	if (OPTIMUM_SHINYEFFECT > 0) {
	if ((renderFlags & ReflectiveBitMask) != 0) {
		outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2 * fogAmount + 2*(1-b), 0, 1));
	}
	glow += pow(max(0.0, dot(normal, lightPosition)), 6) * 0.125 * shadowIntensity * (1 - fogAmount - murkiness);
	}


#if GBUFFER == 1
	outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
	outGNormal = gnormal;
	if (OPTIMUM_OPTIMUMAO > 0) {
	// Optimum AO class channel (C.5): the no-cull opaque pass (plants, grass, cross-quads) is thin.
	}
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

	outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
#if TAAMOTION > 0
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, 0.0, gl_FragCoord.z);
#endif
}
