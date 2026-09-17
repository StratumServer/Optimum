#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquidmotion.fsh (docs/vulkan-native-shaders.md).
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
// TAA-PLAN.md rule 7 states this ("writes the surface's motion and depth").
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
#include "chunkliquidmotion.interface.glsl"

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
