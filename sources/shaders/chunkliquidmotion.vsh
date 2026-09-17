#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Optimum TAA (P4): the liquid velocity pass (TAA-PLAN.md accuracy rule 7).
//
// The OIT liquid draw cannot write Primary's motion attachment - it renders
// into the Transparent target and its six oit.fsh outputs already fill that
// framebuffer's attachment set - so the liquid pools are drawn a second time,
// into Primary, by this program, which writes nothing but the motion
// attachment (ChunkRenderer.RenderLiquidMotion opens the draw-buffer window
// with every other colour attachment masked out).
//
// The whole point is that the position this program computes is the SAME
// position chunkliquid.vsh computed for the same vertex: same liquid wave warp,
// same divisor from the same water flags, and the same "pretend the surface is
// closer" w-offset at the end. Anything else and the velocity pass would
// depth-test against a surface a fraction of a pixel away from the one that was
// shaded, and the motion vector would belong to a neighbouring fragment.
//
// The previous position follows accuracy rule 4, exactly as chunkopaque.vsh
// does: the chunk's camera-relative position moved by the camera's own motion,
// the warp re-evaluated through previousWarpState(), and the previous
// UNJITTERED projection with the previous CameraMatrixOrigin.

layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlags;
layout(location = 4) in vec2 flowVector;
layout(location = 5) in int colormapData;
layout(location = 6) in int waterFlagsIn;

uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

#if TAAMOTION > 0
uniform mat4 prevProjectionMatrix;   // previous frame's UNJITTERED world projection
uniform mat4 prevModelViewMatrix;    // previous frame's CameraMatrixOrigin
uniform vec3 cameraPosDelta;         // cameraPos(this frame) - cameraPos(previous frame)
out vec4 taaPrevClip;
#endif

#include vertexflagbits.ash
#include vertexwarp.vsh


// chunkliquid.vsh's position path, verbatim, as a function of the warp state so
// the same code can be evaluated for this frame and for the previous one. The
// vanilla body reads:
//
//   if ((waterFlagsIn & 1) == 1) {
//       float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) ? 90 : 5;
//       float oceanity = ((waterFlagsIn >> 2) & 0xff) * OneOver255;
//       div *= max(0.2, 1 - oceanity);
//       worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div);
//   }
//   else if ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) {
//       worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, 90);
//   }
//
// with applyLiquidWarping being the currentWarpState() wrapper of the overload
// called here.
vec4 taaLiquidWorldPos(WarpState st, vec4 worldPos)
{
	if ((waterFlagsIn & 1) == 1) {
		float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) ? 90 : 5;

		float oceanity = ((waterFlagsIn >> 2) & 0xff) * OneOver255;
		div *= max(0.2, 1 - oceanity);

		worldPos = applyLiquidWarpingState(st, (waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div);
	}
	else if ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) {
		worldPos = applyLiquidWarpingState(st, (waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, 90);
	}

	return worldPos;
}


void main(void)
{
	vec4 truePos = vec4(xyz + origin, 1.0);

	vec4 worldPos = taaLiquidWorldPos(currentWarpState(), truePos);
	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

	// chunkliquid.vsh's last line: the liquid surface is pretended to be closer
	// than it is, so it always wins against stairs and slabs beside it. It moves
	// where the fragment lands, so it belongs on both clip positions.
	gl_Position.w += 0.0008 / max(0.1, gl_Position.z);

#if TAAMOTION > 0
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);
		taaPrevPos = taaLiquidWorldPos(taaPrev, taaPrevPos);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);
		taaPrevClip.w += 0.0008 / max(0.1, taaPrevClip.z);
	}
#endif
}
