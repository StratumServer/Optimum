#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of entityanimated.vsh (the Optimum override in sources/shaders, docs/vulkan.md).
//
// Axes: TAAMOTION (the previous-position varying and the AnimationPrev buffer), GBUFFER (SSAOLEVEL > 0: the
// G-buffer varyings) and USEOIT. The GLSL 330 vertex stage does not test USEOIT, but the client creates the
// AnimationPrev UBO only for the opaque program (ShaderProgramEntityanimated: `!Oit && EffectiveTaa`), and the
// OIT fragment stage never reads taaPrevClip. So the buffer and the previous-position reconstruction are
// compiled only for TAAMOTION == 1 && USEOIT == 0; the OIT variant writes a taaPrevClip nothing reads.
//
// The Animation blocks are storage buffers at set 2 (section 3) with the same members; the bone array is a
// runtime array because MAXANIMATEDELEMENTS is no longer a define. Its index (jointId) is unchanged.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of entityanimated.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
//
// Axes: USEOIT (oit.glsl's six outputs instead of the opaque set), GBUFFER (SSAOLEVEL > 0), TAAMOTION and
// ALLOWDEPTHOFFSET (the first-person hands' gl_FragDepth write). SHADOWQUALITY, NORMALVIEW and SHINYEFFECT gate
// no declaration and are specialization-constant branches with the same expressions.
//
// The vertex stage includes fogandlight.vert.glsl and vertexwarp.glsl, so the names fogandlight.frag.glsl and
// this body read from those owners (flatFogDensity, viewDistance, fogSpheres, windWaveCounter, ...) are frame
// members here (section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of entityanimated (docs/vulkan.md), shared by the opaque
// program, Entityanimated_Oit (USEOIT axis) and the first-person hands (ALLOWDEPTHOFFSET axis).
//
// Entity placement (section 4, about 280 B of DRAW data per entity): the push block holds the sampler slot,
// then the small per-entity integers and the TAA flags in declaration order (vertex stage first). The record
// holds the matrices, the colours and every remaining scalar: entityanimated.vsh's uniforms, the prev*
// uniforms of vertexwarp.glsl, then entityanimated.fsh's uniforms and underwatereffects.glsl's frameSize, each
// in declaration order. Names the TAAMOTION, USEOIT and ALLOWDEPTHOFFSET blocks declared are declared
// unconditionally: collectUniformNames reads the unpreprocessed text, so every variant has all of them.
//
// A block member cannot carry the GLSL 330 initializers (frostAlpha = 0, taaHistoryValid = 0,
// taaReactive = 0.0, alphaTest = 0.001, and vertexwarp's prev* defaults); the runtime seeds them
// (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, entityTex);

    int addRenderFlags;
    int extraGlow;
    int taaHistoryValid;

    float taaReactive;
    int entityId;
    int glitchFlicker;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    vec4 renderColor;
    float frostAlpha;
    mat4 projectionMatrix;
    mat4 viewMatrix;
    mat4 modelMatrix;
    int skipRenderJointId;
    int skipRenderJointId2;
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

    vec2 taaRenderSize;
    vec2 taaJitterPx;
    float alphaTest;
    float glitchEffectStrength;
    float depthOffset;

    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

// UBO:Animation,0,4800
layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION) readonly buffer Animation
{
    mat4 values[];
} ElementTransforms;

#if TAAMOTION == 1
#if USEOIT == 0
// UBO:AnimationPrev,1,4800
layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION_PREV) readonly buffer AnimationPrev
{
    mat4 values[];
} PrevElementTransforms;
#endif

layout(location = 14) out vec4 taaPrevClip;
#endif

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec3 vertexPosition;
layout(location = 5) out vec4 worldPos;
layout(location = 6) out float damageEffect;
layout(location = 7) out vec4 camPos;
layout(location = 8) out float fragFrostAlpha;
layout(location = 9) flat out int renderFlags;

layout(location = 10) out vec4 glPos;

layout(location = 11) out vec3 normal;
#if GBUFFER == 1
layout(location = 12) out vec4 fragPosition;
layout(location = 13) out vec4 gnormal;
#endif


#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	damageEffect = damageEffectIn;
	mat4 animModelMat = modelMatrix * ElementTransforms.values[jointId];
	worldPos = animModelMat * vec4(vertexPositionIn, 1.0);

	renderFlags = flags | addRenderFlags;

	if ((renderFlags & WindModeFruitMask) > 0) {
		fragFrostAlpha = frostAlpha / 4;
		renderFlags &= ~WindModeFruitMask;
	} else fragFrostAlpha = frostAlpha;

	if ((renderFlags & WindModeWaterMask) > 0) {
		worldPos = applyLiquidWarping(true, worldPos, 5);
	} else {
		worldPos = applyVertexWarping(renderFlags, worldPos);
	}
	worldPos = applyGlobalWarping(worldPos);


#if TAAMOTION == 1
#if USEOIT == 0
	// Placed here, before the local `int renderFlags = extraGlow + flags;` below
	// shadows the flat output: the warp branch has to see the same flags the
	// current position was warped with, fruit-mask clearing included.
	{
		vec4 taaPrevWorld;
		if (taaHistoryValid != 0) {
			mat4 taaPrevAnimMat = prevModelMatrix * PrevElementTransforms.values[jointId];
			taaPrevWorld = taaPrevAnimMat * vec4(vertexPositionIn, 1.0);
			WarpState taaPrev = previousWarpState();
			if ((renderFlags & WindModeWaterMask) > 0) {
				taaPrevWorld = applyLiquidWarpingState(taaPrev, true, taaPrevWorld, 5);
			} else {
				taaPrevWorld = applyVertexWarpingState(taaPrev, renderFlags, taaPrevWorld);
			}
			taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
		} else {
			// Treat the surface as static in the world: its camera-relative position
			// a frame ago differed by the camera's own movement only (accuracy rule 4).
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);
	}
#else
	// Entityanimated_Oit: no AnimationPrev buffer, and its fragment stage never reads the varying.
	taaPrevClip = vec4(0.0);
#endif
#endif

	vertexPosition = vertexPositionIn.xyz * 1.5;

	vec4 cameraPos = camPos = viewMatrix * worldPos;

	uv = uvIn;
	int renderFlags = extraGlow + flags;
	color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;

	// Distance fade out
	color.a *= clamp(20 * (1.05 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	gl_Position = projectionMatrix * cameraPos;
	calcShadowMapCoords(viewMatrix, worldPos);


	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	normal = unpackNormal(renderFlags);
	normal = (animModelMat * vec4(normal.x, normal.y, normal.z, 0)).xyz;

	#if GBUFFER == 1
		fragPosition = cameraPos;
		gnormal = viewMatrix * vec4(normal, 0);
	#endif

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 2) in vec4 rgbaFog;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in vec3 vertexPosition;
layout(location = 9) flat in int renderFlags;
layout(location = 11) in vec3 normal;
layout(location = 5) in vec4 worldPos;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
layout(location = 7) in vec4 camPos;
layout(location = 6) in float damageEffect;
layout(location = 8) in float fragFrostAlpha;

// Our include system is dumb and does not do conditional includes
// So we add a OIT preprocceor test to oit.fsh as well
#include "oit.glsl"

#if USEOIT == 0
	layout(location = 0) out vec4 outColor;
	layout(location = 1) out vec4 outGlow;
	#if GBUFFER == 1
	layout(location = 12) in vec4 fragPosition;
	layout(location = 13) in vec4 gnormal;
	layout(location = 2) out vec4 outGNormal;
	layout(location = 3) out vec4 outGPosition;
	#endif
#endif

// TAA motion vectors (Optimum P3); see chunkopaque.fsh for the contract.
// The alpha channel is this fragment's WINDOW depth, which for the first-person
// hand and item programs is gl_FragCoord.z + depthOffset, not gl_FragCoord.z -
// they write gl_FragDepth below, and the resolve compares what it finds here
// against the depth buffer. Writing the un-offset value would make the resolve
// reject every hand pixel and fall back to camera reprojection on the one class
// of geometry whose motion differs most from the camera's.
#if TAAMOTION == 1
layout(location = 14) in vec4 taaPrevClip;
#if USEOIT == 0
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif
#endif

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "noise2d.glsl"
#include "underwatereffects.glsl"

void main() {
	float b = 1;

	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	vec4 texColor = texture(optimumTextures2D[entityTex], uv);

	// Declared before the branch that assigns it (contract section 5); every path overwrites the 0.
	float intensity = 0.0;
	if (OPTIMUM_SHADOWQUALITY > 0) {
	intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	} else {
	intensity = 0.45;
	}


	//float seed = mod(entityId, 1000) / 5.0; - this is broken on NVIDIA cards O_O
	int eidfloor = (entityId / 100) * 100;
	float seed = (entityId - eidfloor) / 5.0;

	texColor = applyFrostEffect(fragFrostAlpha, texColor, normal, vertexPosition + vec3(seed));
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition + vec3(seed), 0);

	texColor *= color;
	texColor.rgb *= b;

#if USEOIT == 1
	vec4 outColor;
#endif

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(texColor, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity, worldPos.xyz);
	}


	if (glitchFlicker >0 && glitchEffectStrength > 0) {
		float g = gnoise(vec3(gl_FragCoord.y / 2.0, gl_FragCoord.x / 2.0, windWaveCounter*30 + entityId * 3));
		outColor.a *= mix(1, clamp(0.7 + g / 2, 0, 1), glitchEffectStrength);

		float b = gnoise(vec3(0, 0, windWaveCounter*60 + entityId * 3));
		outColor.a *= mix(1, clamp(b * 10 + 2, 0, 1), glitchEffectStrength);
	}

	if (OPTIMUM_NORMALVIEW == 0) {
	if (outColor.a < alphaTest) discard;
	}



	float glow = 0;
	if (OPTIMUM_SHINYEFFECT > 0) {
	outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1)), outColor, min(1, 2 * fogAmount));
	}

#if USEOIT == 0 && GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, 0);
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}



#if USEOIT == 1
	OIT(outColor, glowLevel+glow);
#else
	outGlow = vec4(glowLevel + glow, 0, 0, color.a);
#endif



#if ALLOWDEPTHOFFSET == 1
	// This likely tanks performance in any other scenario so we do only only for the first person mode rendering. See also https://www.khronos.org/opengl/wiki/Early_Fragment_Test#Limitations
	gl_FragDepth = gl_FragCoord.z + depthOffset;

	// A bit hacky: We use ALLOWDEPTHOFFSET for the first person rendering. SSAO seems to break on it, so we disable it
	#if USEOIT == 0 && GBUFFER == 1
		outGPosition.w=1;
	if (OPTIMUM_OPTIMUMAO > 0) {
		// Optimum AO class channel (C.5, C.9): the first-person hand writes the hand class.
		outGNormal.w = -1.0;
	}
	#endif

#endif


#if TAAMOTION == 1 && USEOIT == 0
	// Opaque skinned entities are not reactive on their own; the C# side raises
	// taaReactive to 1 for a draw whose per-entity history was unusable, where
	// the vector above is camera-only and the history must not be trusted.
	#if ALLOWDEPTHOFFSET == 1
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaReactive, clamp(gl_FragCoord.z + depthOffset, 0.0, 1.0));
	#else
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaReactive, gl_FragCoord.z);
	#endif
#endif
}

#endif
