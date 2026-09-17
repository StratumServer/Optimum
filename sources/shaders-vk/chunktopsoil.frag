#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktopsoil.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: GBUFFER (G-buffer outputs; the motion output moves from 2 to 4 with it), TAAMOTION (motion output,
// written through include/motion.glsl). SHADOWQUALITY, NORMALVIEW and SHINYEFFECT are
// specialization-constant branches.
//
// Optimum override of the vanilla chunktopsoil.fsh: adds the TAA motion-vector
// output (P3). Everything else is vanilla, line for line.
//
// fogandlight.frag.glsl and fogspheres.glsl read names owned by fogandlight.vsh and vertexwarp.vsh, which the
// vertex stage includes, so this stage activates those owners' names itself (contract section 3).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunktopsoil.interface.glsl"
#include "varyings.glsl"

layout(location = 0) in vec4 rgba;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in float fogAmount;
layout(location = 3) in vec2 uv;
layout(location = 4) in vec2 uv2;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
layout(location = 9) in vec4 worldPos;
layout(location = 8) in vec3 vertexPosition;

layout(location = 10) flat in int renderFlags;
layout(location = 5) in vec3 normal;
#if GBUFFER == 1
layout(location = 7) in vec4 gnormal;
#endif



layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 6) in vec4 fragPosition;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

// TAA motion vectors (Optimum P3); see chunkopaque.frag for the contract.
#if TAAMOTION > 0
layout(location = 11) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "colormap.frag.glsl"
#include "noise3d.glsl"
#include "underwatereffects.glsl"

void main()
{
	vec4 brownSoilColor = texture(optimumTextures2D[terrainTex], uv) * rgba;

      	if (normal.y >= 0) {
      		 // Top (normal.y == 1) or Sides (normal.y == 0)
      		vec4 grassColor = getColorMapped(optimumTextures2D[terrainTexLinear], texture(optimumTextures2D[terrainTex], uv2 + vec2(blockTextureSize.x * normal.y, 0))) * rgba;
      		outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
      	} else {
      		 // Bottom
      		outColor = applyFog(brownSoilColor, fogAmount);
	}

	if (psychedelicStrength > Epsilon) outColor = applyPsychedelicEffect(outColor, vertexPosition*2, 0);
	if (glitchStrength > Epsilon) outColor = applyRustEffect(outColor, normal, vertexPosition, 1);


	// Declared before the branch that assigns it (contract section 5); every path overwrites the 0.45.
	float intensity = 0.45;
	if (OPTIMUM_SHADOWQUALITY > 0) {
	intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	} else {
	intensity = 0.45;
	}



	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowWithNormal(outColor, clamp(fogAmount - 50*murkiness, 0, 1), normal, 1, intensity, worldPos.xyz);
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

	outColor.a = rgbaFog.a;

	float aTest = outColor.a;
	aTest += max(0.0, 1 - rgba.a) * min(1, outColor.a * 10);
	if (OPTIMUM_NORMALVIEW == 0) {
	 // Fade to sky color
         // Also, when looking through tinted glass you can clearly see the edges where we fade to sky color; using the outColor.a < 0.005 discard seems to completely fix that
	if (aTest < alphaTest || outColor.a < 0.005) discard;
	}


	float glow = 0;

	if (OPTIMUM_SHINYEFFECT > 0) {
	if ((renderFlags & ReflectiveBitMask) > 0) {
		vec3 worldVec = normalize(worldPos.xyz);

		float angle = 2 * dot(normalize(normal), worldVec);
		angle += gnoise(vec3(uv.x*500, uv.y*500, worldVec.z/10)) / 7.5;
		outColor.rgb *= max(vec3(1), vec3(1) + 3*blockLight * gnoise(vec3(worldVec.x/10 + angle, worldVec.y/10 + angle, worldVec.z/10 + angle)));
	}

	glow = pow(max(0.0, dot(normal, lightPosition)), 6) * 0.1 * shadowIntensity * (1 - fogAmount);
	}



#if GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount * 2 + glowLevel);
	outGNormal = gnormal;
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

    outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);
#if TAAMOTION > 0
	// Opaque terrain is not reactive.
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, 0.0, gl_FragCoord.z);
#endif
}
