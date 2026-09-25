#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of standard.vsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axes: GLOWSUB (the glowSub vertex input; `#if defined(GLOWSUB)` becomes `#if GLOWSUB == 1`), GBUFFER
// (SSAOLEVEL > 0) and TAAMOTION.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of standard.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axes: GBUFFER (SSAOLEVEL > 0), TAAMOTION and ALLOWDEPTHOFFSET (`#if defined(ALLOWDEPTHOFFSET)` with
// `ALLOWDEPTHOFFSET > 0` inside becomes `#if ALLOWDEPTHOFFSET == 1`). BLOOM, NORMALVIEW and SHINYEFFECT gate
// no declaration and are specialization-constant branches with the same expressions; the G-buffer write the
// GLSL 330 source guards with a second `#if SSAOLEVEL > 0` is behind the GBUFFER axis, which is that test.
//
// The vertex stage includes fogandlight.vert.glsl and vertexwarp.glsl, so the names fogandlight.frag.glsl reads
// from those owners (flatFogDensity, viewDistance, fogSpheres, windWaveCounter, ...) are frame members here
// (section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of standard (docs/vulkan.md). Held items, dropped items and
// block-entity models: at most a few draws per Use(), so the push block holds the two sampler slots (tex, then
// tex2dOverlay, the GLSL 330 unit order) and the integer flags plus taaReactive, in declaration order (vertex
// stage first). The record holds everything else: standard.vsh's uniforms, vertexwarp.glsl's prev* uniforms,
// standard.fsh's uniforms and underwatereffects.glsl's frameSize, each in declaration order. Names inside the
// TAAMOTION and ALLOWDEPTHOFFSET blocks are declared unconditionally (collectUniformNames reads the
// unpreprocessed text).
//
// A block member cannot carry the GLSL 330 initializers (taaHistoryValid = 0, applySsao = 1,
// taaReactive = 0.0, extraGodray = 0, alphaTest = 0.001, ssaoAttn = 0, damageEffect = 0, and vertexwarp's
// prev* defaults); the runtime seeds them (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2dOverlay);

    int extraGlow;
    int dontWarpVertices;
    int fadeFromSpheresFog;
    int addRenderFlags;
    int taaHistoryValid;

    int applySsao;
    int tempGlowMode;
    int normalShaded;
    int skyShaded;
    float taaReactive;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaTint;
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaGlowIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 viewMatrix;
    mat4 modelMatrix;
    float extraZOffset;
    mat4 prevProjectionMatrix;
    mat4 prevViewMatrix;
    mat4 prevModelMatrix;
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

    float extraGodray;
    float alphaTest;
    float ssaoAttn;
    float overlayOpacity;
    vec2 overlayTextureSize;
    vec2 baseTextureSize;
    vec2 baseUvOrigin;
    float damageEffect;
    float depthOffset;
    vec4 averageColor;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
#if GLOWSUB == 1
layout(location = 4) in float glowSub;
#endif

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out vec4 rgbaGlow;
layout(location = 4) out float fogAmount;
layout(location = 5) out vec4 camPos;
layout(location = 6) out vec4 worldPos;
layout(location = 7) flat out int renderFlags;

layout(location = 8) out vec3 normal;
#if GBUFFER == 1
layout(location = 9) out vec4 gnormal;
#endif

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION == 1
layout(location = 10) out vec4 taaPrevClip;
#endif


#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);

	if (dontWarpVertices == 0) {
		worldPos = applyVertexWarping(flags | addRenderFlags, worldPos);
		worldPos = applyGlobalWarping(worldPos);
	}
	if (dontWarpVertices == 2) {
		int windMode = ((flags | addRenderFlags) >> WindModePosition) & 0xF;
		vec4 newPos = applyVertexWarping(flags | addRenderFlags, worldPos);
		worldPos = mix(worldPos, newPos, 0.25); // Hardcoded intensity downscale of 4x
		worldPos = applyGlobalWarping(worldPos);
	}

#if TAAMOTION == 1
	// The same vertex, one frame ago. The warp branch below has to be the caller's
	// exact branch - a held item passes dontWarpVertices 2, a dropped item 0 - or
	// the two positions differ by a warp the object never had.
	{
		vec4 taaPrevWorld;
		if (taaHistoryValid != 0) {
			taaPrevWorld = prevModelMatrix * vec4(vertexPositionIn, 1.0);
			WarpState taaPrev = previousWarpState();
			if (dontWarpVertices == 0) {
				taaPrevWorld = applyVertexWarpingState(taaPrev, flags | addRenderFlags, taaPrevWorld);
				taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
			}
			if (dontWarpVertices == 2) {
				vec4 taaNewPos = applyVertexWarpingState(taaPrev, flags | addRenderFlags, taaPrevWorld);
				taaPrevWorld = mix(taaPrevWorld, taaNewPos, 0.25); // same hardcoded 4x downscale as above
				taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
			}
		} else {
			// Treat the surface as static in the world: its camera-relative position
			// a frame ago differed by the camera's own movement only (accuracy rule 4).
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);
		// The z-fighting nudge applies to both positions or the pair disagrees by it.
		taaPrevClip.w += extraZOffset;
	}
#endif

	camPos = viewMatrix * worldPos;

	uv = uvIn;

	float gs = 0.0;
#if GLOWSUB == 1
	gs = glowSub;
#endif

	int glow = clamp(extraGlow + (flags & GlowLevelBitMask) - int(gs * 255), 0, 255);

	renderFlags = glow | (flags & ~GlowLevelBitMask);
	rgbaGlow.rgb = rgbaGlowIn.rgb * max(vec3(0), (1 - vec3(3*gs)));
	rgbaGlow.a = rgbaGlowIn.a;

	color = rgbaTint * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos) * colorIn;
#if GLOWSUB == 1
	color.rgb *= 1 - 0.5 * gs;
	color.rgb = mix(color.rgb, rgbaGlow.rgb, max(0, glowLevel - gs) / 2);
#endif


	if (fadeFromSpheresFog > 0) {
		color.a *= clamp(1 - getSpheresFogAmount(vertexPositionIn * 10), 0, 1);
	}

	// Distance fade out
	color.a *= clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	rgbaFog = rgbaFogIn;
	gl_Position = projectionMatrix * camPos;
	calcShadowMapCoords(viewMatrix, worldPos);

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	gl_Position.w += extraZOffset;

	normal = unpackNormal(flags);
	normal = normalize((modelMatrix * vec4(normal.x, normal.y, normal.z, 0)).xyz);

	#if GBUFFER == 1
		gnormal = viewMatrix * vec4(normal, 0);
	#endif

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 9) in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#if TAAMOTION == 1
layout(location = 10) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 2) in vec4 rgbaFog;
layout(location = 4) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 3) in vec4 rgbaGlow;
layout(location = 5) in vec4 camPos;
layout(location = 6) in vec4 worldPos;
layout(location = 8) in vec3 normal;
layout(location = 7) flat in int renderFlags;


#include "fogandlight.frag.glsl"
#include "noise2d.glsl"
#include "underwatereffects.glsl"

void main() {
	float b = 1;

	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	if (overlayOpacity > 0) {
		vec2 uvOverlay = (uv - baseUvOrigin) * (baseTextureSize / overlayTextureSize);

		vec4 col1 = texture(optimumTextures2D[tex2dOverlay], uvOverlay);
		vec4 col2 = texture(optimumTextures2D[tex], uv);

		float a1 = overlayOpacity * col1.a  * min(1, col2.a * 100);
		float a2 = col2.a * (1 - a1);

		outColor = vec4(
		  (a1 * col1.r + col2.r * a2) / (a1+a2),
		  (a1 * col1.b + col2.g * a2) / (a1+a2),
		  (a1 * col1.g + col2.b * a2) / (a1+a2),
		  a1 + a2
		) * color;

	} else {
		outColor = texture(optimumTextures2D[tex], uv) * color;
	}

	if (OPTIMUM_BLOOM == 0) {
	outColor.rgb *= 1 + glowLevel;
	}

	if (tempGlowMode == 1) {
		float f = (averageColor.r+averageColor.g+averageColor.b) / (rgbaGlow.r+rgbaGlow.g+rgbaGlow.b);
		f=max(f,0.6);
		// Use multiply so some texture is still visible, use 'f' to adjust to same brightness
		outColor.rgb = mix(outColor.rgb, outColor.rgb * rgbaGlow.rgb / f, min(1.5, glowLevel*2));

	} else {
		outColor.rgb = mix(outColor.rgb, rgbaGlow.rgb, glowLevel * rgbaGlow.a);
	}

	if (normalShaded > 0) {
		float b = min(1, getBrightnessFromNormal(normal, 1, 0.45) + min(0.5, glowLevel));
		outColor *= vec4(b, b, b, 1);
	}

	float murkiness=skyShaded > 0 ? getSkyMurkiness() : getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadow(outColor, 0);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadow(outColor, fogAmount);
	}

	if (OPTIMUM_NORMALVIEW == 0) {
	if (outColor.a < alphaTest) discard;
	}

	float glow = 0;
	if (OPTIMUM_SHINYEFFECT > 0) {
	outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1)), outColor, min(1, 2 * fogAmount));
	glow = pow(max(0.0, dot(normal, lightPosition)), 6) / 8 * shadowIntensity * (1 - fogAmount);
	}

#if GBUFFER == 1
	if (applySsao > 0) {
		outGPosition = vec4(camPos.xyz, fogAmount + glowLevel);
	} else {
		outGPosition = vec4(camPos.xyz, 1);
	}
	outGNormal = vec4(gnormal.xyz, ssaoAttn);

#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

	outColor.rgb *= b;
	outGlow = vec4(glowLevel + glow, extraGodray - fogAmount, 0, outColor.a);

#if ALLOWDEPTHOFFSET == 1
	// This likely tanks performance in any other scenario so we do only only for the first person mode rendering. See also https://www.khronos.org/opengl/wiki/Early_Fragment_Test#Limitations
	gl_FragDepth = gl_FragCoord.z + depthOffset;

	// A bit hacky: We use ALLOWDEPTHOFFSET for the first person rendering. SSAO seems to break on it, so we disable it
	#if GBUFFER == 1
		outGPosition.w=1;
	if (OPTIMUM_OPTIMUMAO > 0) {
		// Optimum AO class channel (C.5, C.9): the hand view has its own projection; the AO pass
		// leaves these pixels at visibility 1 and treats them as solid when sampled.
		outGNormal.w = -1.0;
	}
	#endif
#endif

#if TAAMOTION == 1
	// Items and block-entity models are not reactive on their own; the C# side
	// raises taaReactive to 1 for a draw whose per-object history was unusable,
	// where the vector above is camera-only and the history must not be trusted.
	#if ALLOWDEPTHOFFSET == 1
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaReactive, clamp(gl_FragCoord.z + depthOffset, 0.0, 1.0));
	#else
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaReactive, gl_FragCoord.z);
	#endif
#endif
}

#endif
