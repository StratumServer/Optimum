#version 330 core

// Optimum override of the vanilla decals.fsh (TAA P4).
//
// Decals are the crack overlay on a block being broken (and the rot/soot
// overlays). They are drawn on Primary in the AfterOIT stage with the depth
// test AND the depth mask on, and decals.vsh pretends they are closer than the
// block under them so they always win the z-fight:
//
//     gl_Position.w += zOffset * 0.00025 / max(0.1, gl_Position.z * 0.05);
//
// That is why the attachment cannot simply be left alone. The terrain writer
// put the block's vector there with a = the BLOCK's window depth; the decal
// then overwrites the depth buffer with its own, slightly nearer value, and
// taa-resolve.fsh accepts a vector only while
// abs(motion.a - depth) <= max(2e-4, 8e-4 * depth). Close to the camera that
// offset is larger than the tolerance, so every near decal would silently
// demote its block to the camera fallback - which is exact for a static block
// but wrong for the swaying ones the offset was raised for in the first place
// ("not enough for leaves :o", decals.vsh).
//
// So the decal writes the motion itself: the SAME surface motion the block has
// (the chunk previous path of accuracy rule 4, replayed through the same
// vertexwarp functions), with a = its own gl_FragCoord.z, which is exactly what
// lands in the depth buffer. The pixel is then accepted at any distance.
//
// Vanilla's own lines below are untouched; the whole addition preprocesses away
// when TAAMOTION is 0.

in vec2 decalUv;
in vec2 blockUv;
in vec2 decalUvSize;
in vec4 color;
in vec2 decalUvStart;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

uniform sampler2D decalTexture;
uniform sampler2D blockTexture;

// TAA motion vectors (Optimum P4). TAAMOTIONLOCATION is the Primary colour
// attachment the motion texture occupies (2 without the SSAO G-buffer, 4 with
// it); SystemRenderDecals opens the draw-buffer window that lets this
// attachment be written at all, and the blend seam forces replace blending on
// it - the decal pass draws with blending ON, and a blended motion vector is a
// weighted average of two surfaces' displacements, which belongs to neither.
#if TAAMOTION > 0
in vec4 taaPrevClip;
uniform vec2 taaRenderSize;   // render-target size in pixels
uniform vec2 taaJitterPx;     // this frame's sub-pixel shear, in pixels
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
#endif

void main()
{
	vec2 uv = vec2(decalUvStart.x + mod(decalUv.x, decalUvSize.x), decalUvStart.y + mod(decalUv.y, decalUvSize.y));
	
	outColor = color * texture(decalTexture, uv);
	
	
	float blockAlpha = texture(blockTexture, blockUv).a;
	if (outColor.a < 0.01 || blockAlpha < 0.01) discard;

	outGlow = vec4(0, 0, 0, outColor.a);

#if TAAMOTION > 0
	// b = 0: a decal is an opaque overlay on a static-or-swaying block surface
	// and its vector is that surface's own, so the history is trustworthy. The
	// one thing that does change without moving is the crack stage advancing to
	// the next texture, and TAA-PLAN.md's inventory row leaves that to the
	// resolve's neighbourhood clipping ("crack progress rejected by colour
	// clipping") rather than throwing the whole pixel's history away every time
	// a block is being hit.
	//
	// A previous position behind the previous camera is not a motion vector; a
	// zero alpha routes the pixel to the resolve's camera fallback, exactly as
	// in chunkopaque.fsh.
	if (taaPrevClip.w <= 1e-6) {
		outMotion = vec4(0.0);
	} else {
		vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;
		vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;
		outMotion = vec4(prevPixel - currentPixel, 0.0, gl_FragCoord.z);
	}
#endif
}
