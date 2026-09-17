#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of standard.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
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
#include "standard.interface.glsl"

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
