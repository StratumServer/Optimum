#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of entityanimated.vsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
//
// Axes: TAAMOTION (the previous-position varying and the AnimationPrev buffer), GBUFFER (SSAOLEVEL > 0: the
// G-buffer varyings) and USEOIT. The GLSL 330 vertex stage does not test USEOIT, but the client creates the
// AnimationPrev UBO only for the opaque program (ShaderProgramEntityanimated: `!Oit && EffectiveTaa`), and the
// OIT fragment stage never reads taaPrevClip. So the buffer and the previous-position reconstruction are
// compiled only for TAAMOTION == 1 && USEOIT == 0; the OIT variant writes a taaPrevClip nothing reads.
//
// The Animation blocks are storage buffers at set 2 (section 3) with the same members; the bone array is a
// runtime array because MAXANIMATEDELEMENTS is no longer a define. Its index (jointId) is unchanged.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "entityanimated.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

// UBO:Animation,0,4800
layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION) readonly buffer Animation
{
    mat4 values[];
} ElementTransforms;

#if TAAMOTION == 1
#if USEOIT == 0
// UBO:AnimationPrev,1,4800
layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION_PREV) readonly buffer AnimationPrev
{
    mat4 values[];
} PrevElementTransforms;
#endif

layout(location = 14) out vec4 taaPrevClip;
#endif

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec3 vertexPosition;
layout(location = 5) out vec4 worldPos;
layout(location = 6) out float damageEffect;
layout(location = 7) out vec4 camPos;
layout(location = 8) out float fragFrostAlpha;
layout(location = 9) flat out int renderFlags;

layout(location = 10) out vec4 glPos;

layout(location = 11) out vec3 normal;
#if GBUFFER == 1
layout(location = 12) out vec4 fragPosition;
layout(location = 13) out vec4 gnormal;
#endif


#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	damageEffect = damageEffectIn;
	mat4 animModelMat = modelMatrix * ElementTransforms.values[jointId];
	worldPos = animModelMat * vec4(vertexPositionIn, 1.0);

	renderFlags = flags | addRenderFlags;

	if ((renderFlags & WindModeFruitMask) > 0) {
		fragFrostAlpha = frostAlpha / 4;
		renderFlags &= ~WindModeFruitMask;
	} else fragFrostAlpha = frostAlpha;

	if ((renderFlags & WindModeWaterMask) > 0) {
		worldPos = applyLiquidWarping(true, worldPos, 5);
	} else {
		worldPos = applyVertexWarping(renderFlags, worldPos);
	}
	worldPos = applyGlobalWarping(worldPos);


#if TAAMOTION == 1
#if USEOIT == 0
	// Placed here, before the local `int renderFlags = extraGlow + flags;` below
	// shadows the flat output: the warp branch has to see the same flags the
	// current position was warped with, fruit-mask clearing included.
	{
		vec4 taaPrevWorld;
		if (taaHistoryValid != 0) {
			mat4 taaPrevAnimMat = prevModelMatrix * PrevElementTransforms.values[jointId];
			taaPrevWorld = taaPrevAnimMat * vec4(vertexPositionIn, 1.0);
			WarpState taaPrev = previousWarpState();
			if ((renderFlags & WindModeWaterMask) > 0) {
				taaPrevWorld = applyLiquidWarpingState(taaPrev, true, taaPrevWorld, 5);
			} else {
				taaPrevWorld = applyVertexWarpingState(taaPrev, renderFlags, taaPrevWorld);
			}
			taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
		} else {
			// Treat the surface as static in the world: its camera-relative position
			// a frame ago differed by the camera's own movement only (accuracy rule 4).
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);
	}
#else
	// Entityanimated_Oit: no AnimationPrev buffer, and its fragment stage never reads the varying.
	taaPrevClip = vec4(0.0);
#endif
#endif

	vertexPosition = vertexPositionIn.xyz * 1.5;

	vec4 cameraPos = camPos = viewMatrix * worldPos;

	uv = uvIn;
	int renderFlags = extraGlow + flags;
	color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;

	// Distance fade out
	color.a *= clamp(20 * (1.05 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	gl_Position = projectionMatrix * cameraPos;
	calcShadowMapCoords(viewMatrix, worldPos);


	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	normal = unpackNormal(renderFlags);
	normal = (animModelMat * vec4(normal.x, normal.y, normal.z, 0)).xyz;

	#if GBUFFER == 1
		fragPosition = cameraPos;
		gnormal = viewMatrix * vec4(normal, 0);
	#endif

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
