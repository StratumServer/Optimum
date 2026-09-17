#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of decals.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// The motion writer goes through include/motion.glsl with reactive 0 and writer depth gl_FragCoord.z; its
// behind-camera result vec4(0, 0, 0, 0) is the GLSL 330 vec4(0.0). GBUFFER is an axis only because it moves
// the motion attachment (TAAMOTIONLOCATION 4 with the G-buffer, 2 without).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "decals.interface.glsl"

layout(location = 0) in vec2 decalUv;
layout(location = 1) in vec2 blockUv;
layout(location = 2) in vec2 decalUvSize;
layout(location = 4) in vec4 color;
layout(location = 3) in vec2 decalUvStart;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

#if TAAMOTION == 1
layout(location = 5) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#endif

#include "motion.glsl"

void main()
{
	vec2 uv = vec2(decalUvStart.x + mod(decalUv.x, decalUvSize.x), decalUvStart.y + mod(decalUv.y, decalUvSize.y));

	outColor = color * texture(optimumTextures2D[decalTexture], uv);


	float blockAlpha = texture(optimumTextures2D[blockTexture], blockUv).a;
	if (outColor.a < 0.01 || blockAlpha < 0.01) discard;

	outGlow = vec4(0, 0, 0, outColor.a);

#if TAAMOTION == 1
	// b = 0: a decal overlays a static-or-swaying block surface and its vector is that surface's own.
	// a = gl_FragCoord.z, the depth the decal itself writes.
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, 0.0, gl_FragCoord.z);
#endif
}
