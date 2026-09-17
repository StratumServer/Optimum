#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlescube.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// SHADOWQUALITY is a specialization-constant branch. The motion writer is the section 7 exception: behind the
// previous camera it keeps reactive 1 (optimumWriteReactiveOnly(1.0) is the GLSL 330 vec4(0, 0, 1, 0)), and
// otherwise it keeps its writer depth, calling optimumMotionVector directly.
// The vertex stage includes fogandlight.vsh and vertexwarp.vsh, which own the flatFogDensity, fogSpheres and
// windWaveCounter that fogandlight.fsh reads here (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "particlescube.interface.glsl"

layout(location = 0) in vec4 color;
layout(location = 15) in vec2 uv;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 3) in float fogAmount;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in vec3 normal;
layout(location = 4) in vec4 worldPos;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 6) in vec4 fragPosition;
layout(location = 7) in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

// TAAMOTIONLOCATION: 4 with the SSAO G-buffer, 2 without.
#if TAAMOTION == 1
layout(location = 5) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#endif

#include "fogandlight.frag.glsl"
#include "underwatereffects.glsl"
#include "motion.glsl"

void main()
{
	// Declared before the branch that assigns it (contract section 5); both paths overwrite the 0.
	float intensity = 0.0;
	if (OPTIMUM_SHADOWQUALITY > 0) {
	intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	} else {
	intensity = 0.45;
	}



	float murkiness = getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(color, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadowWithNormal(color, fogAmount, normal, 1, intensity, worldPos.xyz);
	}

	outGlow = vec4(glowLevel, 0, 0, outColor.a);
	//outColor = vec4((normal.x + 1) / 2.0, (normal.y + 1) / 2.0, (normal.z + 1) / 2.0, 1);

#if GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, outColor.a);
#endif

#if TAAMOTION == 1
	// b = 1: a cube particle is always reactive. a = gl_FragCoord.z: cube particles write depth, so this is
	// the writer depth the resolve accepts. Behind the previous camera rg and a are zero and b stays 1.
	if (taaPrevClip.w <= 1e-6) {
		outMotion = optimumWriteReactiveOnly(1.0);
	} else {
		outMotion = vec4(optimumMotionVector(taaPrevClip, taaRenderSize, taaJitterPx), 1.0, gl_FragCoord.z);
	}
#endif
}
