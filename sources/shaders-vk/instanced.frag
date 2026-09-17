#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of instanced.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: GBUFFER (SSAOLEVEL > 0) and TAAMOTION. NORMALVIEW gates no declaration and is a
// specialization-constant branch.
//
// The vertex stage includes fogandlight.vert.glsl, so flatFogDensity, viewDistance and the fog spheres are frame
// members here (section 3, cross-stage owners); windWaveCounter has no owner in this program and is a record
// member.
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "instanced.interface.glsl"

layout(location = 0) in vec4 color;
layout(location = 1) in vec2 uv;
layout(location = 2) in vec4 rgbaFog;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 8) flat in int renderFlags;
layout(location = 4) in vec3 normal;
layout(location = 5) in vec4 worldPos;
// Never written by instanced.vsh and never read here (GLSL 330 links it as an unused input); the optimised
// module drops it.
layout(location = 11) in float normalShadeIntensity;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 6) in vec4 fragPosition;
layout(location = 7) in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


#if TAAMOTION == 1
layout(location = 9) in vec4 taaPrevClip;
layout(location = 10) in float taaInstanceReactive;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif

#include "fogandlight.frag.glsl"

void main () {
  outColor = texture(optimumTextures2D[tex], uv) * color;
  if (outColor.a < alphaTest) discard;

  outColor = applyFogAndShadowWithNormal(outColor, fogAmount, normal, 1, 0.45, worldPos.xyz);

  //outColor = vec4((normal.x + 0.5) / 2, (normal.y + 0.5)/2, (normal.z+0.5)/2, 1);

  outGlow = vec4(glowLevel, 0, 0, outColor.a);

#if GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = gnormal;
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

#if TAAMOTION == 1
	// Mechanical blocks are opaque and not reactive on their own; the C# side
	// stamps reactive 1 per instance when its history was unusable, where the
	// vector above is camera-only and the history must not be trusted.
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaInstanceReactive, gl_FragCoord.z);
#endif

}
