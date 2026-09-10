#version 330 core
// Optimum override of the vanilla entityanimated.vsh: adds the TAA motion-vector
// writer for skinned entities (P3). Everything else is vanilla, line for line.
//
// The previous position is the same skinning run twice: previous model matrix x
// previous bone matrix from the AnimationPrev block, then the SAME warp branch
// with the previous frame's WarpState, then the previous unjittered projection
// for whichever view this draw belongs to (world FOV, or the hand FOV for the
// first-person hands). Without usable history - the entity spawned, its animator
// or mesh changed, it was off-screen last frame, or the camera switched between
// first and third person - the vertex falls back to camera-only motion and C#
// raises taaReactive so the resolve leans on this frame instead.
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;
uniform vec4 renderColor;
uniform int addRenderFlags;
uniform float frostAlpha = 0;
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;
uniform int extraGlow;

// No longer needed but kept to not break mods that still assign these values
uniform int skipRenderJointId;
uniform int skipRenderJointId2;

// UBO:Animation,0,4800
layout (std140) uniform Animation
{
    mat4 values[MAXANIMATEDELEMENTS];	// MAXANIMATEDELEMENTS constant is defined during game engine shader loading.
} ElementTransforms;

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
uniform mat4 prevProjectionMatrix;   // previous frame's UNJITTERED projection for this draw's view
uniform mat4 prevViewMatrix;         // previous frame's CameraMatrixOrigin (the view entities draw under)
uniform mat4 prevModelMatrix;        // this renderer's model matrix as it was last frame
uniform vec3 cameraPosDelta;         // cameraPos(this frame) - cameraPos(previous frame)
uniform int taaHistoryValid = 0;     // 0 = no usable per-entity history, use camera-only motion

// UBO:AnimationPrev,1,4800
layout (std140) uniform AnimationPrev
{
    mat4 values[MAXANIMATEDELEMENTS];
} PrevElementTransforms;

out vec4 taaPrevClip;
#endif

out vec2 uv;
out vec4 color;
out vec4 rgbaFog;
out float fogAmount;
out vec3 vertexPosition;
out vec4 worldPos;
out float damageEffect;
out vec4 camPos;
out float fragFrostAlpha;
flat out int renderFlags;

out vec4 glPos;

out vec3 normal;
#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif


#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

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
	

#if TAAMOTION > 0
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
	
	#if SSAOLEVEL > 0
		fragPosition = cameraPos;
		gnormal = viewMatrix * vec4(normal, 0);
	#endif
}