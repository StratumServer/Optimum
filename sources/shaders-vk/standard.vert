#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of standard.vsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: GLOWSUB (the glowSub vertex input; `#if defined(GLOWSUB)` becomes `#if GLOWSUB == 1`), GBUFFER
// (SSAOLEVEL > 0) and TAAMOTION.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "standard.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
#if GLOWSUB == 1
layout(location = 4) in float glowSub;
#endif

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out vec4 rgbaGlow;
layout(location = 4) out float fogAmount;
layout(location = 5) out vec4 camPos;
layout(location = 6) out vec4 worldPos;
layout(location = 7) flat out int renderFlags;

layout(location = 8) out vec3 normal;
#if GBUFFER == 1
layout(location = 9) out vec4 gnormal;
#endif

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION == 1
layout(location = 10) out vec4 taaPrevClip;
#endif


#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);

	if (dontWarpVertices == 0) {
		worldPos = applyVertexWarping(flags | addRenderFlags, worldPos);
		worldPos = applyGlobalWarping(worldPos);
	}
	if (dontWarpVertices == 2) {
		int windMode = ((flags | addRenderFlags) >> WindModePosition) & 0xF;
		vec4 newPos = applyVertexWarping(flags | addRenderFlags, worldPos);
		worldPos = mix(worldPos, newPos, 0.25); // Hardcoded intensity downscale of 4x
		worldPos = applyGlobalWarping(worldPos);
	}

#if TAAMOTION == 1
	// The same vertex, one frame ago. The warp branch below has to be the caller's
	// exact branch - a held item passes dontWarpVertices 2, a dropped item 0 - or
	// the two positions differ by a warp the object never had.
	{
		vec4 taaPrevWorld;
		if (taaHistoryValid != 0) {
			taaPrevWorld = prevModelMatrix * vec4(vertexPositionIn, 1.0);
			WarpState taaPrev = previousWarpState();
			if (dontWarpVertices == 0) {
				taaPrevWorld = applyVertexWarpingState(taaPrev, flags | addRenderFlags, taaPrevWorld);
				taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
			}
			if (dontWarpVertices == 2) {
				vec4 taaNewPos = applyVertexWarpingState(taaPrev, flags | addRenderFlags, taaPrevWorld);
				taaPrevWorld = mix(taaPrevWorld, taaNewPos, 0.25); // same hardcoded 4x downscale as above
				taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
			}
		} else {
			// Treat the surface as static in the world: its camera-relative position
			// a frame ago differed by the camera's own movement only (accuracy rule 4).
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);
		// The z-fighting nudge applies to both positions or the pair disagrees by it.
		taaPrevClip.w += extraZOffset;
	}
#endif

	camPos = viewMatrix * worldPos;

	uv = uvIn;

	float gs = 0.0;
#if GLOWSUB == 1
	gs = glowSub;
#endif

	int glow = clamp(extraGlow + (flags & GlowLevelBitMask) - int(gs * 255), 0, 255);

	renderFlags = glow | (flags & ~GlowLevelBitMask);
	rgbaGlow.rgb = rgbaGlowIn.rgb * max(vec3(0), (1 - vec3(3*gs)));
	rgbaGlow.a = rgbaGlowIn.a;

	color = rgbaTint * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos) * colorIn;
#if GLOWSUB == 1
	color.rgb *= 1 - 0.5 * gs;
	color.rgb = mix(color.rgb, rgbaGlow.rgb, max(0, glowLevel - gs) / 2);
#endif


	if (fadeFromSpheresFog > 0) {
		color.a *= clamp(1 - getSpheresFogAmount(vertexPositionIn * 10), 0, 1);
	}

	// Distance fade out
	color.a *= clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	rgbaFog = rgbaFogIn;
	gl_Position = projectionMatrix * camPos;
	calcShadowMapCoords(viewMatrix, worldPos);

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	gl_Position.w += extraZOffset;

	normal = unpackNormal(flags);
	normal = normalize((modelMatrix * vec4(normal.x, normal.y, normal.z, 0)).xyz);

	#if GBUFFER == 1
		gnormal = viewMatrix * vec4(normal, 0);
	#endif

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
