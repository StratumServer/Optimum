#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquidmotion.vsh (docs/vulkan-native-shaders.md). Variant axis: TAAMOTION (the
// taaPrevClip varying and the previous-position replay).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunkliquidmotion.interface.glsl"

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
//
// prevProjectionMatrix: previous frame's UNJITTERED world projection; prevModelViewMatrix: previous frame's
// CameraMatrixOrigin; cameraPosDelta: cameraPos(this frame) - cameraPos(previous frame).

layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlags;
layout(location = 4) in vec2 flowVector;
layout(location = 5) in int colormapData;
layout(location = 6) in int waterFlagsIn;

#if TAAMOTION == 1
layout(location = 0) out vec4 taaPrevClip;
#endif

#include "vertexflagbits.glsl"
#include "vertexwarp.glsl"


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

#if TAAMOTION == 1
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);
		taaPrevPos = taaLiquidWorldPos(taaPrev, taaPrevPos);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);
		taaPrevClip.w += 0.0008 / max(0.1, taaPrevClip.z);
	}
#endif

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	// It runs after the w offset above, as the rewriter's wrapper does; taaPrevClip is a varying, not a
	// clip position, and is left in GL convention, which is what the motion arithmetic expects.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
