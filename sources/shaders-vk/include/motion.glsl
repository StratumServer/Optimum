// The only writer of the motion attachment in native shaders
// (docs/vulkan-native-shaders.md section 7, docs/temporal-frame-contract.md section 3.2).
//
//   rg = motion vector: previous pixel - current unjittered pixel, render pixels
//   b  = reactive [0,1]
//   a  = writer depth, window depth [0,1]
//
// A program reconstructs its previous clip position itself (warp replay, skinning,
// instance transforms, liquid waves, z-offset replay); this file starts where that
// position exists. Only the current pixel is jitter-corrected: the previous projection
// is the previous frame's UNJITTERED projection, so there is nothing to remove from the
// previous side, and passing a jittered previous matrix here would be a caller bug.
//
// The arithmetic is the three lines every GLSL 330 writer uses (chunkopaque.fsh
// taaMotionVector and its copies), in the same order, so the vector is bit-identical.
//
// Fragment stage only (reads gl_FragCoord). Written in the common subset of GLSL 330
// and 450 so the GPU test can drive it through the rewriter as well.

#ifndef OPTIMUM_MOTION_GLSL
#define OPTIMUM_MOTION_GLSL

// The raw vector, without the behind-camera test. particlescube calls this directly: on
// its behind-camera branch it keeps its writer depth and its reactive value of 1.
vec2 optimumMotionVector(vec4 prevClip, vec2 renderSize, vec2 jitterPx)
{
	vec2 prevPixel = (prevClip.xy / prevClip.w * 0.5 + 0.5) * renderSize;
	vec2 currentPixel = gl_FragCoord.xy - jitterPx;
	return prevPixel - currentPixel;
}

// A previous position behind the previous camera (w <= 1e-6) is not a vector: rg and a
// are zero, so the resolve falls back to camera reprojection, and b survives, because
// the resolve reads reactive whether or not the pixel passed the validity test.
vec4 optimumWriteMotion(vec4 prevClip, vec2 renderSize, vec2 jitterPx, float reactive, float writerDepth)
{
	if (prevClip.w <= 1e-6) return vec4(0.0, 0.0, reactive, 0.0);
	return vec4(optimumMotionVector(prevClip, renderSize, jitterPx), reactive, writerDepth);
}

// A writer with no vector at all (the OIT merge): rg and a are zero, which under the
// merge's additive blend leaves the opaque vector underneath bit-for-bit intact.
vec4 optimumWriteReactiveOnly(float reactive)
{
	return vec4(0.0, 0.0, reactive, 0.0);
}

#endif
