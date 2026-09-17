#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of instanced.vsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: TAAMOTION (the previous-transform instance attributes and varyings) and GBUFFER (SSAOLEVEL > 0).
// The per-instance attributes keep their GLSL 330 locations 4-13.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "instanced.interface.glsl"

layout(location = 0) in vec3 vertexPosition;  // Per vertex
layout(location = 1) in vec2 uvIn;				// Per vertex
layout(location = 2) in vec4 rgbaBlockIn;		// Per vertex (rgb = block light, a=sun light level)
layout(location = 3) in int renderFlagsIn; 	// Per vertex

layout(location = 4) in vec4 rgbaLightIn;		// Per instance
layout(location = 5) in mat4 transform;	 	// Per instance

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION == 1
layout(location = 9) in mat4 prevTransform;		// Per instance: this device's transform last frame
layout(location = 13) in vec4 taaInstanceMeta;	// Per instance: x = history usable, y = reactive
#endif

layout(location = 0) out vec4 color;
layout(location = 1) out vec2 uv;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec3 normal;
layout(location = 5) out vec4 worldPos;

#if GBUFFER == 1
layout(location = 6) out vec4 fragPosition;
layout(location = 7) out vec4 gnormal;
#endif

layout(location = 8) flat out int renderFlags;

#if TAAMOTION == 1
layout(location = 9) out vec4 taaPrevClip;
layout(location = 10) out float taaInstanceReactive;
#endif

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main()
{
	worldPos = transform * vec4(vertexPosition, 1.0);
	vec4 cameraPos = modelViewMatrix * worldPos;

#if TAAMOTION == 1
	// The same vertex, one frame ago. instanced.vsh applies no vertex warp and no
	// w-offset, so the previous position is the previous instance transform run
	// through the previous camera - nothing else has to be replayed.
	{
		vec4 taaPrevWorld;
		if (taaInstanceMeta.x != 0.0) {
			taaPrevWorld = prevTransform * vec4(vertexPosition, 1.0);
		} else {
			// Treat the block as static in the world: its camera-relative position
			// a frame ago differed by the camera's own movement only (accuracy rule 4).
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevWorld);
		taaInstanceReactive = taaInstanceMeta.y;
	}
#endif


	calcShadowMapCoords(modelViewMatrix, worldPos);

	uv = uvIn;
	color = applyLight(rgbaAmbientIn, rgbaLightIn * rgbaBlockIn, renderFlagsIn, cameraPos);
	rgbaFog = rgbaFogIn;

	// Distance fade out
	color.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5, -1, 1);
	gl_Position = projectionMatrix * cameraPos;

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	renderFlags = renderFlagsIn;

	normal = unpackNormal(renderFlagsIn);
	normal = normalize((transform * vec4(normal.x, normal.y, normal.z, 0)).xyz);

	#if GBUFFER == 1
	fragPosition = cameraPos;
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	#endif

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
