#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquidmotion.vsh (docs/vulkan.md). Variant axis: TAAMOTION (the
// taaPrevClip varying and the previous-position replay).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquidmotion.fsh (docs/vulkan.md).
//
// Variant axes: TAAMOTION (the output set and the taaPrevClip varying) and GBUFFER (TAAMOTIONLOCATION: 4 with
// the G-buffer, else 2). The TAA-off variant keeps the GLSL 330 dummy output: one vec4 at location 0 holding
// zero. The motion value goes through include/motion.glsl (section 7): optimumWriteMotion's behind-camera
// branch returns the same vec4(0, 0, reactive, 0) the GLSL 330 early return wrote, and its vector is the same
// three lines in the same order; the dummy's vec4(0.0) is optimumWriteReactiveOnly(0.0).
//
// Optimum TAA (P4): the fragment half of the liquid velocity pass.
//
// This shader writes ONE attachment - Primary's motion attachment - and no
// colour, no glow and no G-buffer: ChunkRenderer.RenderLiquidMotion masks every
// other attachment out of the draw-buffer set for the duration of the pass, on
// both backends, so the shaded image the OIT merge already produced is left
// exactly as it was.
//
// It does write depth. The OIT liquid draw cannot: LoadFrameBuffer(Transparent)
// disables the depth mask, so the water surface never reaches Primary's depth
// attachment (which the Transparent target shares) and Primary's depth at a
// water pixel is the opaque surface BEHIND the water. The resolve accepts a
// motion vector only where the writer's own window depth matches the depth
// buffer within abs(a - depth) <= max(2e-4, 8e-4 * depth), so a velocity pass
// that left depth alone would have every one of its vectors rejected and the
// water would fall back to camera reprojection - which is the ghosting this
// pass exists to remove. Writing the surface's depth here makes the two agree
// and gives the resolve the water surface's own linear depth for its
// disocclusion test, which is the depth the motion vector belongs to.
// The temporal contract in docs/vulkan.md states this ("writes the surface's motion and depth").
//
// rg = previousPixel - currentUnjitteredPixel in render pixels, b = reactive,
// a = this fragment's window depth - the same contract chunkopaque.fsh writes.
//
// Record (chunkliquidmotion.interface.glsl): taaRenderSize is the render-target size in pixels, taaJitterPx
// this frame's sub-pixel shear in pixels. taaLiquidReactive: foam, flow-UV scrolling and the specular sparkle
// animate in place: the surface does not move, but its shading does, so history that reprojects perfectly
// still has to be weighted down. 0.3 is the plan's starting value (Conventions: "animated liquid textures 0.3
// initial, tuned by measurement").
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunkliquidmotion (docs/vulkan.md). A chunk-family program: one
// draw per mesh pool per Use(), so the DRAW uniforms origin and modelViewMatrix sit in the push block (76 B, no
// samplers). The record holds the rest: chunkliquidmotion.vsh's uniforms in declaration order, then the
// vertexwarp.glsl optimum-program-uniform names (vertexwarp.vsh owns only the current-frame members; the prev*
// mirrors are program uniforms), then chunkliquidmotion.fsh's.
//
// The GLSL 330 sources declare prevProjectionMatrix, prevModelViewMatrix, cameraPosDelta, taaRenderSize,
// taaJitterPx and taaLiquidReactive inside #if TAAMOTION > 0; the oracle reads the unpreprocessed text, so they
// are names of every variant and are declared unconditionally. The GLSL 330 initializers (taaLiquidReactive
// = 0.3 and vertexwarp's prev* defaults) are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 prevProjectionMatrix;
    mat4 prevModelViewMatrix;
    vec3 cameraPosDelta;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;

    vec2 taaRenderSize;
    vec2 taaJitterPx;
    float taaLiquidReactive;
};

#if defined(OPTIMUM_VERTEX)


// Optimum TAA (P4): the liquid velocity pass (the temporal contract in docs/vulkan.md).
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

#elif defined(OPTIMUM_FRAGMENT)


#if TAAMOTION == 1
layout(location = 0) in vec4 taaPrevClip;

#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#else
// TAA off: this program is never used - ChunkRenderer.RenderLiquidMotion
// returns before binding it - but it is still registered and compiled, and a
// fragment stage with no output at all is not worth handing to two different
// shader translators. One dummy attachment keeps it trivially valid.
layout(location = 0) out vec4 outMotion;
#endif

#include "motion.glsl"


void main()
{
#if TAAMOTION == 1
	// A previous position behind the previous camera is not a motion vector; a
	// zero alpha routes the pixel to the resolve's camera fallback, exactly as
	// in chunkopaque.fsh. Depth is still written for it, because the fragment
	// is genuinely the visible surface either way.
	//
	// The reactive value is delivered anyway: taa-resolve.fsh reads motion.b
	// whether or not the writer-depth test accepted the pixel (P3 finding (h)),
	// and the foam and flow-UV animation that 0.3 stands for is happening on
	// this fragment regardless of where it was last frame. Zeroing b here would
	// hand a water pixel FULL history weight in exactly the frames the camera
	// swung hardest - the worst case, not the safe one. taa-skymotion.fsh keeps
	// its reactive value on the same branch for the same reason.
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, clamp(taaLiquidReactive, 0.0, 1.0), gl_FragCoord.z);
#else
	outMotion = optimumWriteReactiveOnly(0.0);
#endif
}

#endif
