#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of entityanimated.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
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
#include "entityanimated.interface.glsl"

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
