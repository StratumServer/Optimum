#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktransparent.fsh (vanilla, docs/vulkan-native-shaders.md). Axis: USEOIT, through
// include/oit.glsl, which declares the six OIT outputs and OIT() only when it is 1. The client registers
// chunktransparent with Oit = true (ShaderProgramBase's default), so USEOIT=0 is never linked; the GLSL 330
// program has no outputs and no OIT() there either, so the 0 variant skips the call and writes nothing.
// SHINYEFFECT is a specialization-constant branch.
//
// fogandlight.frag.glsl and fogspheres.glsl read names owned by fogandlight.vsh and vertexwarp.vsh, which the
// vertex stage includes, so this stage activates those owners' names itself (contract section 3).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunktransparent.interface.glsl"
#include "varyings.glsl"

layout(location = 0) in vec4 rgba;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in float fogAmount;
layout(location = 3) in vec2 uv;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in vec4 worldPos;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
layout(location = 5) in vec3 vertexPos;

layout(location = 8) in float normalShadeIntensity;
layout(location = 6) flat in int renderFlags;
layout(location = 7) flat in vec3 normal;

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "colormap.frag.glsl"
#include "underwatereffects.glsl"
#include "oit.glsl"

void main()
{
	// When looking through tinted glass you can clearly see the edges where we fade to sky color
	// Using this discard seems to completely fix that
	if (rgba.a < 0.005) discard;

	vec4 texColor = rgba * getColorMapped(optimumTextures2D[terrainTex], texture(optimumTextures2D[terrainTex], uv));

	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPos.xyz, 0);

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		texColor = applyFogAndShadowWithNormal(texColor, 0, normal, normalShadeIntensity, 0.45, worldPos.xyz);
		texColor.rgb = applyUnderwaterEffects(texColor.rgb, murkiness);
	} else {
		texColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, normalShadeIntensity, 0.45, worldPos.xyz);
	}


	if (OPTIMUM_SHINYEFFECT > 0) {
	float glow=0;
	texColor = mix(applyReflectiveEffect(texColor, glow, renderFlags, uv, normal, worldPos, worldPos, blockLight), texColor, min(1, 2 * fogAmount));
	}

#if USEOIT > 0
    OIT(texColor, glowLevel);
#endif

}
