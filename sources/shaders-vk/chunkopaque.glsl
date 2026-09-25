#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkopaque.vsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axes: USESSBO (attribute layout and FaceData), GREEDYMESH (tile varyings), GBUFFER (gnormal), TAAMOTION
// (taaPrevClip). SHADOWQUALITY is a specialization-constant branch.
//
// lightPosition and shadowIntensity belong to fogandlight.fsh, which the fragment stage includes, so this
// stage activates that owner's names itself (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_FSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkopaque.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
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
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunkopaque (docs/vulkan.md). One draw per mesh pool: the
// push block holds the two sampler slots in chunkopaque.fsh's declaration order, then origin and
// modelViewMatrix (84 B). The record holds every other uniform, chunkopaque.vsh's first (the previous-frame
// warp state vertexwarp.glsl reads comes with its include), then chunkopaque.fsh's and underwatereffects'
// frameSize.
//
// Every uniform is declared whatever the axes: the GLSL 330 name set does not depend on defines.
// cameraUnderwater, shadowIntensity and lightPosition, which chunkopaque.vsh declares itself, are frame
// members here (their owners underwatereffects.fsh and fogandlight.fsh are included by the fragment stage).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTexLinear);
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogDensityIn;
    float fogMinIn;
    mat4 projectionMatrix;
    float subpixelPaddingX;
    float subpixelPaddingY;
    mat4 prevProjectionMatrix;
    mat4 prevModelViewMatrix;
    vec3 cameraPosDelta;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;

    float alphaTest;
    float horizonFog;
    vec3 sunPosition;
    float dayLight;
    int haxyFade;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in int colormapData;
 #endif

layout(location = 0) out vec4 rgba;
layout(location = 1) out vec2 uv;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec3 normal;
layout(location = 5) out vec3 vertexPosition;
layout(location = 6) out vec4 worldPos;
layout(location = 7) out vec4 camPos;
// With every axis on the program has 17 varyings of its own, one more than locations 0-15 hold, so the
// two scalar floats share location 8 as components 0 and 1.
layout(location = 8, component = 0) out float lod0Fade;
layout(location = 8, component = 1) out float nb;

// Greedy mesh tile repeat (Optimum): tile counts and sub-texture bounds.
// Compiled in only when the feature is on (GREEDYMESH stamped from
// OptimumConfig at shader load); at 0 this whole shader preprocesses to
// vanilla, so disabled greedy meshing costs nothing.
#if GREEDYMESH > 0
layout(location = 10) flat out int tileWidth;
layout(location = 11) flat out int tileHeight;
layout(location = 12) flat out vec2 tileBoundsMin;
layout(location = 13) flat out vec2 tileBoundsSize;
#endif

 #if GBUFFER == 1
layout(location = 14) out vec4 gnormal;
 #endif


layout(location = 9) flat out int renderFlags;

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
layout(location = 15) out vec4 taaPrevClip;
#endif

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"
#include "colormap.vert.glsl"

 #if USESSBO > 0
layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexIndex / 4];
	int vIndex = gl_VertexIndex & 0x03;
	renderFlags = vdata.flags[vIndex];
	vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	vertexPosition = xyz;
 #endif

#if GREEDYMESH > 0
	// Decode greedy tile counts from flags (Optimum).
	// Bit 11 (ReflectiveBitMask) is the sentinel: set only on greedy-
	// tiled quads (eligible blocks are never reflective). When set,
	// bits 29-31 = tileWidth - 1, bits 8-10 = tileHeight - 1.
	// When clear, this is a vanilla vertex: do not touch those bits.
	int greedyTiled = (renderFlags >> 11) & 1;
	if (greedyTiled != 0) {
		tileWidth = ((renderFlags >> 29) & 0x7) + 1;
		tileHeight = ((renderFlags >> 8) & 0x7) + 1;
		// Clear sentinel + tile bits so downstream code (normal unpack,
		// wind, zoffset) does not misinterpret them.
		renderFlags = renderFlags & ~(0x7 << 29) & ~(0x7 << 8) & ~(1 << 11);
	} else {
		tileWidth = 1;
		tileHeight = 1;
	}
#endif

	vec4 truePos = vec4(vertexPosition + origin, 1.0);
	bool isLeaves = ((renderFlags & WindModeBitMask) > 0);

	worldPos = applyVertexWarping(renderFlags, truePos);
	worldPos = applyGlobalWarping(worldPos);

	camPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * camPos;

	calcShadowMapCoords(modelViewMatrix, worldPos);
 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);

#if GREEDYMESH > 0
	// Extract tile sub-texture bounds from FaceData for the fragment
	// shader's tiling wrap. vdata.uv is the origin (vertex 0 UV packed
	// as 16-bit fixed point), vdata.uvSize carries the delta to vertex 2.
	// Unpack to float atlas coords matching UnpackUv's scale.
	if (greedyTiled != 0) {
		tileBoundsMin = vec2(vdata.uv & 0xFFFF, vdata.uv >> 16 & 0xFFFF) / 32768.0;
		int uvs = vdata.uvSize;
		// Preserve sign: negative delta means the axis is inverted
		// (vertex 0 sits at the high end, vertex 2 at the low end).
		// The fragment shader needs this to map fract(position) correctly.
		tileBoundsSize = vec2(
			(uvs & 0x7FFF) - ((uvs & 0x4000) << 1),
			(uvs >> 16 & 0x7FFF) - ((uvs & 0x40000000) >> 15)
		) / 32768.0;
	} else {
		tileBoundsMin = vec2(0.0);
		tileBoundsSize = vec2(0.0);
	}
#endif
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = uvIn;

#if GREEDYMESH > 0
	// GL 3.3 path has no channel to carry tile bounds (would need an extra
	// vertex attribute via CustomFloats, which the opaque pass's MeshData
	// doesn't allocate). The emitter (OptimumGreedyMeshEmitter) forces
	// 1x1-only merges whenever UseSSBOs is false, so greedyTiled should
	// never be set here - this just forces the tile count back to 1x1 as
	// a second guard, so a merged quad can never reach the fragment
	// shader's tileBoundsSize division (which would be 0/0 = NaN) even if
	// that invariant is ever violated (e.g. SSBOs toggled mid-session
	// before chunks retesselate).
	if (greedyTiled != 0) {
		tileWidth = 1;
		tileHeight = 1;
	}
	tileBoundsMin = vec2(0.0);
	tileBoundsSize = vec2(0.0);
#endif
 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);

	// Distance fade out
	rgba.a = clamp(17.0 - 20.0 * length(worldPos.xz) / viewDistance + max(0.0, worldPos.y * 0.02), -1.0, 1.0);

	rgbaFog = rgbaFogIn;

	normal = unpackNormal(renderFlags);

#if GBUFFER == 1
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	gnormal.w = isLeaves ? 1 : 0;
	// Bit 15 of colour-map metadata is Optimum's explicit thin-block class
	// for non-wind chunk geometry. Wind-mode geometry is already thin; the
	// same bit retains its vanilla season-offset meaning there.
	if (OPTIMUM_OPTIMUMAO > 0 && !isLeaves) {
#if USESSBO > 0
		if ((vdata.colormapData & 0x8000) != 0) gnormal.w = 1;
#else
		if ((colormapData & 0x8000) != 0) gnormal.w = 1;
#endif
	}
#endif


	// To fix Z-Fighting on blocks over certain other blocks
	if (gl_Position.z > -1) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / ((gl_Position.z + 3) * 0.05);
	}


#if TAAMOTION > 0
	// The same vertex, one frame ago, through the same code path: the chunk's
	// camera-relative position moved by exactly the camera's own motion
	// (accuracy rule 4), the warp is re-evaluated with the previous frame's
	// counters, and the previous unjittered projection replaces this frame's
	// jittered one. The warp noise consumes an absolute-ish position, which is
	// prevRel + prevPlayerpos - that is what previousWarpState() carries.
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);
		taaPrevPos = applyVertexWarpingState(taaPrev, renderFlags, taaPrevPos);
		taaPrevPos = applyGlobalWarpingState(taaPrev, taaPrevPos);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);

		// The z-fighting w-offset shifts where the fragment lands on screen, so
		// leaving it off the previous position would report that shift as motion.
		if (taaPrevClip.z > -1) {
			int taaPrevZOffset = (renderFlags & ZOffsetBitMask) >> 8;
			taaPrevClip.w += taaPrevZOffset * 0.00025 / ((taaPrevClip.z + 3) * 0.05);
		}
	}
#endif


	if ((renderFlags & Lod0BitMask) != 0) {
		float b = clamp(10 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 2.5, 0.0, 1.0);
		lod0Fade = 1 - b;
	}
	else    lod0Fade = 0.0;


	// Declared before the branch that assigns it (contract section 5); every path overwrites the 0.45.
	float intensity = 0.45;
	if (OPTIMUM_SHADOWQUALITY > 0) {
		intensity = 0.34 + (1 - shadowIntensity)/8.0;
	} else {
		intensity = 0.45;
	}
	nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

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
		// Optimum AO class channel (docs/vulkan.md#ambient-occlusion C.5): the
		// vertex stage's wind flag or explicit per-block class marks thin geometry.
		// Snow layers stay solid even when drawn alongside cross quads.
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
	// Optimum AO class channel (C.5) comes from the vertex, not the render pool.
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

#endif
