#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of instanced.vsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axes: TAAMOTION (the previous-transform instance attributes and varyings) and GBUFFER (SSAOLEVEL > 0).
// The per-instance attributes keep their GLSL 330 locations 4-13.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of instanced.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
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
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of instanced (docs/vulkan.md). Every per-object value travels as
// an instance attribute (locations 4-13), and the uniforms are set once per Use(), so the push block holds only
// the sampler slot and every uniform is a record member: instanced.vsh's, then instanced.fsh's, then
// fogandlight.frag.glsl's windWaveCounter (no vertexwarp owner in this program), each in declaration order.
//
// A block member cannot carry alphaTest's GLSL 330 initializer (0.1); the runtime seeds it (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    mat4 prevProjectionMatrix;
    mat4 prevModelViewMatrix;
    vec3 cameraPosDelta;

    float alphaTest;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    float windWaveCounter;
};

#if defined(OPTIMUM_VERTEX)


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

#elif defined(OPTIMUM_FRAGMENT)


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

#endif
