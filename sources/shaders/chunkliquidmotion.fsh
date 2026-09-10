#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

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

#if TAAMOTION > 0
in vec4 taaPrevClip;
uniform vec2 taaRenderSize;   // render-target size in pixels
uniform vec2 taaJitterPx;     // this frame's sub-pixel shear, in pixels

// Foam, flow-UV scrolling and the specular sparkle animate in place: the
// surface does not move, but its shading does, so history that reprojects
// perfectly still has to be weighted down. 0.3 is the plan's starting value
// (Conventions: "animated liquid textures 0.3 initial, tuned by measurement").
uniform float taaLiquidReactive = 0.3;

layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
#else
// TAA off: this program is never used - ChunkRenderer.RenderLiquidMotion
// returns before binding it - but it is still registered and compiled, and a
// fragment stage with no output at all is not worth handing to two different
// shader translators. One dummy attachment keeps it trivially valid.
layout(location = 0) out vec4 outMotion;
#endif


void main()
{
#if TAAMOTION > 0
	// A previous position behind the previous camera is not a motion vector; a
	// zero alpha routes the pixel to the resolve's camera fallback, exactly as
	// in chunkopaque.fsh. Depth is still written for it, because the fragment
	// is genuinely the visible surface either way.
	if (taaPrevClip.w <= 1e-6) {
		outMotion = vec4(0.0);
		return;
	}

	vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;
	vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;
	outMotion = vec4(prevPixel - currentPixel, clamp(taaLiquidReactive, 0.0, 1.0), gl_FragCoord.z);
#else
	outMotion = vec4(0.0);
#endif
}
