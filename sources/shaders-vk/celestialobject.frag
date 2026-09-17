#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of celestialobject.fsh (docs/vulkan-native-shaders.md). Axis: GBUFFER (the G-buffer outputs).
// The vertex stage includes fogandlight.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "celestialobject.interface.glsl"

layout(location = 2) in vec2 uv;
layout(location = 3) in vec4 color;
layout(location = 1) in vec4 rgbaFog;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 0) in vec3 vertexPosition;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 4) in vec4 fragPosition;
layout(location = 5) in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


#include "dither.glsl"
#include "fogandlight.frag.glsl"
#include "skycolor.glsl"
#include "underwatereffects.glsl"

void main () {
	vec4 texColor = applyFog(texture(optimumTextures2D[tex], uv) * color, 0);

	if (texColor.a < alphaTest) discard;

	// Now apply moon phase / sun lighting
	float msa = int(moonSunAngle * 0.31830989 + 0.5) * 3.1415927;    // the moonSunAngle rounded to either 0 or Math.Pi, no angles in between
	float dotp = dot(sunPosition, moonPosition);
	vec3 dirsun = normalize(sunPosition - moonPosition * dotp);
	vec2 xyi = (ivec2(uv * 32) - 16) / 32.0;
	float fact = (clamp((xyi.x * cos(msa) - xyi.y * sin(msa) * sign(dirsun.y)), -1, 1) + 1);
	texColor.rgb *= clamp(7.1 - fact * 6.2 - dotp * 1.7, 0.0, texColor.a + 0.05);

	vec4 texGlow = vec4(glowLevel, extraGodray, 0, texColor.a);

	vec4 skyColor = vec4(1);
	vec4 skyGlow = vec4(1);
	float sealevelOffsetFactor = 0.06;

	getSkyColorAt(vertexPosition, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);

	vec3 skyPosNorm = normalize(vertexPosition.xyz);
	float fogAmount =getFogAmountForSky(vertexPosition, skyPosNorm, sealevelOffsetFactor, horizonFog);
	texColor = applyFog(texColor, fogAmount);

	outColor = texColor;
	outColor.a = texColor.a;


	if (weirdMathToMakeMoonLookNicer > 0) {
		float b = pow((outColor.r+outColor.g+outColor.b) / 2.8, 1.4);
		outColor.rgb *= b;

		float v = max(0.0, (1-texColor.a) * min(1, texColor.a*20)/3 - fogAmount * 0.5);
		outGlow = vec4(v, v, 0, max(0.0, 4-7*dayLight));

		outColor.rgb = max(outColor.rgb, skyColor.rgb);
	} else {

		outGlow = max(texGlow, skyGlow);

		outColor.rgb = max(texColor.rgb, skyColor.rgb);
	}

	float murkiness=getSkyMurkiness();
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, 0);
#endif

}
