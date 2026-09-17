#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of nightsky.fsh (docs/vulkan-native-shaders.md). Axis: GBUFFER (the G-buffer outputs).
// outColor has no location in GLSL 330; it is location 0, the one GL and ProgramInterfaceLayout assign.
// worldPosY is read by nothing and written by no vertex stage, as in GLSL 330.
// The vertex stage includes fogandlight.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "nightsky.interface.glsl"

layout(location = 0) in vec3 texCoords;
layout(location = 1) in float worldPosY;
layout(location = 2) in float nightVisionStrengthv;


layout(location = 0) out vec4 outColor;
#if GBUFFER == 1
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


#include "dither.glsl"
#include "fogandlight.frag.glsl"
#include "underwatereffects.glsl"

void main () {
	vec4 skyCol = texture (optimumTexturesCube[ctex], texCoords) + NoiseFromPixelPosition(ivec2(gl_FragCoord.xy), ditherSeed, horizontalResolution);
	skyCol -= 0.03f;
	skyCol.rgb *= 2;
	skyCol.a = max(0.0, 1 - 2*(dayLight - 0.05));

	outColor = skyCol;
	outColor.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;

	float murkiness=getSkyMurkiness();
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if GBUFFER == 1
	outGPosition = vec4(0);
	outGNormal = vec4(0);
#endif

}
