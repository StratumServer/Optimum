#version 330 core
// Optimum override of the vanilla standard.vsh: adds the TAA motion-vector writer
// for everything the standard shader draws (P3) - held items in both hands, the
// first-person item, dropped items and block-entity models such as the quern top.
// Everything else is vanilla, line for line.
//
// The previous position is the same run with previous inputs: the previous model
// matrix this object was drawn with, the SAME dontWarpVertices branch evaluated
// with the previous frame's WarpState, the previous unjittered projection of
// whichever view the draw belongs to (world FOV, or the hand FOV for the
// first-person item), and the same extraZOffset the current clip position gets.
// Without usable history - the object appeared, its mesh changed, it was not
// drawn last frame, or the camera switched between first and third person - the
// vertex falls back to camera-only motion and C# raises taaReactive so the
// resolve leans on this frame instead.
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
#if defined(GLOWSUB)
layout(location = 4) in float glowSub;
#endif

uniform vec4 rgbaTint;
uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 rgbaGlowIn;
uniform vec4 rgbaFogIn;
uniform int extraGlow;
uniform float fogMinIn;
uniform float fogDensityIn;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

uniform int dontWarpVertices;
uniform int fadeFromSpheresFog;
uniform int addRenderFlags;
uniform float extraZOffset;

out vec2 uv;
out vec4 color;
out vec4 rgbaFog;
out vec4 rgbaGlow;
out float fogAmount;
out vec4 camPos;
out vec4 worldPos;
flat out int renderFlags;

out vec3 normal;
#if SSAOLEVEL > 0
out vec4 gnormal;
#endif

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
uniform mat4 prevProjectionMatrix;   // previous frame's UNJITTERED projection for this draw's view
uniform mat4 prevViewMatrix;         // previous frame's CameraMatrixOrigin (the view standard draws use)
uniform mat4 prevModelMatrix;        // this object's model matrix as it was last frame
uniform vec3 cameraPosDelta;         // cameraPos(this frame) - cameraPos(previous frame)
uniform int taaHistoryValid = 0;     // 0 = no usable per-object history, use camera-only motion
out vec4 taaPrevClip;
#endif


#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

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

#if TAAMOTION > 0
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
#if defined(GLOWSUB)
	gs = glowSub;
#endif

	int glow = clamp(extraGlow + (flags & GlowLevelBitMask) - int(gs * 255), 0, 255);
	
	renderFlags = glow | (flags & ~GlowLevelBitMask);
	rgbaGlow.rgb = rgbaGlowIn.rgb * max(vec3(0), (1 - vec3(3*gs)));
	rgbaGlow.a = rgbaGlowIn.a;
	
	color = rgbaTint * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos) * colorIn;
#if defined(GLOWSUB)
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
	
	#if SSAOLEVEL > 0
		gnormal = viewMatrix * vec4(normal, 0);
	#endif
}