#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Optimum TAA (P4): the sky / volumetric-cloud motion and reactive pass.
//
// TAA-PLAN.md's inventory row for "Clouds (volumetric, map), aurora, night sky,
// sun/moon, sky colour" reads "fallback + reactive; sky uses infinite-direction
// reprojection (P4)". This pass is that row.
//
// WHAT ALREADY WORKED WITHOUT IT. Sky colour, the night sky, the sun and the
// moon all draw on Primary with the depth test off or the depth mask off, so
// they leave the depth buffer at 1.0 and never touch the motion attachment.
// taa-resolve.fsh's writer-depth test then fails (motion.a is 0), and its
// camera fallback unprojects a depth of 1 - a point at infinity - and
// reprojects it through the previous view-projection. That is already the
// infinite-direction reprojection those layers need, and it is exact for
// anything painted on the celestial sphere. None of them needs a writer, and
// none is given one.
//
// WHAT DID NOT. Volumetric clouds and the aurora are drawn into the Transparent
// target during the OIT stage, where Primary's motion attachment does not
// exist, and they MOVE independently of the camera: the cloud map scrolls with
// cloudOffset and the aurora's noise animates with auroraCounter. Reprojected
// by camera rotation alone their history lands on the cloud that used to be
// there, and a cloud edge sweeping across the sky smears. They need a reactive
// value, and the only place their coverage is known per pixel is the Transparent
// target's revealage attachment - the same texture transparentcompose.fsh reads
// as `revealage` and turns into `anet`.
//
// So this pass runs on Primary after the OIT merge, covers the sky pixels only
// (see the vertex shader), and writes:
//   rg = the camera-ROTATION-only reprojection of this pixel's view direction,
//   b  = the reactive value, scaled by how much transparent content covers the
//        pixel, and
//   a  = gl_FragCoord.z = 1.0, which matches the depth buffer at every pixel
//        this pass survives, so the resolve accepts the vector instead of
//        recomputing its own.
//
// A clear-sky pixel comes out with coverage 0 and therefore reactive 0: the sky
// gradient is dithered (ShaderProgramSky's DitherSeed) and is exactly the kind
// of static, noisy signal temporal accumulation is best at, so it keeps its
// full history weight.

#if TAAMOTION > 0
uniform sampler2D transparentRevealTex;  // Transparent colour 1: oit.fsh's outReveal
uniform vec2 taaRenderSize;              // render-target size in pixels
uniform vec2 taaJitterPx;                // this frame's sub-pixel shear, in pixels
uniform mat4 taaInvViewProjJittered;     // raster NDC -> camera-relative world (this frame)
uniform mat4 taaPrevViewProj;            // camera-relative world -> previous unjittered clip

// The reactive value a fully covered sky pixel gets. 1 = never trust the
// history there. Partial coverage interpolates towards it from the coverage
// itself, so a wisp at 20% alpha is not treated like a solid cloud bank.
uniform float taaCloudReactive = 1.0;

layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
#else
// TAA off: the pass never runs (ClientPlatformWindows.RenderOptimumSkyMotion
// returns before binding it), but the program is still registered and compiled,
// and a fragment stage with no output at all is not worth handing to two shader
// translators. One dummy attachment keeps it trivially valid.
layout(location = 0) out vec4 outMotion;
#endif

in vec2 texCoord;

void main()
{
#if TAAMOTION > 0
	// The transparent layer's coverage of this pixel, which is 1 - revealage -
	// the identical term transparentcompose.fsh composites with. Clouds
	// multiply their own (1 - density) into that attachment through the
	// per-attachment blend factors SystemRenderOITLayers sets, the aurora and
	// the quad particles through oit.fsh's outReveal, so every transparent
	// thing that can sit in front of the sky is in here.
	float coverage = clamp(1.0 - texelFetch(transparentRevealTex, ivec2(gl_FragCoord.xy), 0).r, 0.0, 1.0);
	float reactive = mix(coverage, clamp(taaCloudReactive, 0.0, 1.0), coverage);

	// The view direction through this raster position. The inverse projection
	// is the JITTERED one, so the direction belongs to the sample that was
	// actually taken, not to the pixel centre.
	vec2 ndc = gl_FragCoord.xy / taaRenderSize * 2.0 - 1.0;
	vec4 farH = taaInvViewProjJittered * vec4(ndc, 1.0, 1.0);
	vec4 nearH = taaInvViewProjJittered * vec4(ndc, -1.0, 1.0);
	// The far point's position is NOT the view direction: CameraMatrixOrigin is
	// a look-at with the eye at LocalEyePos (~1.7 blocks above the origin), so
	// every reconstructed point carries that eye offset, and treating the far
	// point as a direction projected it into a fixed ~0.6 px vertical error on
	// every sky vector (1.7 / 3000 far-plane blocks, at 745 rows over tan 35 deg).
	// far - near cancels the eye position exactly. Kept homogeneous - the
	// difference of the two points scaled by farH.w * nearH.w - so a projection
	// whose far plane sits at infinity (farH.w == 0) still yields a direction.
	// The sign carries the product's sign; farH.w == 0 counts as positive, which
	// is the +farH.xyz the pass has always used at infinity.
	vec3 direction = farH.xyz * nearH.w - nearH.xyz * farH.w;
	if ((farH.w < 0.0) != (nearH.w < 0.0)) direction = -direction;

	// w = 0 drops the previous view-projection's translation column, which is
	// exactly "the camera may have rotated, it may not have moved": a point on
	// the celestial sphere does not parallax.
	vec4 prevClip = taaPrevViewProj * vec4(direction, 0.0);
	if (prevClip.w <= 1e-6) {
		// Behind the previous camera - not a motion vector. A zero alpha routes
		// the pixel back to the resolve's own camera fallback, exactly as in
		// chunkopaque.fsh; the reactive value is still delivered, because
		// taa-resolve.fsh reads motion.b whether or not the pixel was accepted.
		outMotion = vec4(0.0, 0.0, reactive, 0.0);
		return;
	}

	vec2 prevPixel = (prevClip.xy / prevClip.w * 0.5 + 0.5) * taaRenderSize;
	vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;
	outMotion = vec4(prevPixel - currentPixel, reactive, gl_FragCoord.z);
#else
	outMotion = vec4(0.0);
#endif
}
