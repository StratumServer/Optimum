#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of taa-skymotion.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of taa-skymotion.fsh (docs/vulkan.md).
//
// Variant axes: TAAMOTION (the output set) and GBUFFER (TAAMOTIONLOCATION: 4 with the G-buffer, else 2).
// The TAA-off variant keeps the GLSL 330 dummy output: one vec4 at location 0 holding zero. The motion
// value goes through include/motion.glsl (section 7): optimumWriteMotion's behind-camera branch returns
// the same vec4(0, 0, reactive, 0) the GLSL 330 early return wrote, and its vector is the same three
// lines in the same order; the dummy's vec4(0.0) is optimumWriteReactiveOnly(0.0).
//
// Optimum TAA (P4): the sky / volumetric-cloud motion and reactive pass.
//
// The temporal contract in docs/vulkan.md calls for sky direction reprojection
// with reactive coverage. This pass supplies that coverage.
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
//
// Record (taa-skymotion.interface.glsl): transparentRevealTex is Transparent colour 1 (oit.fsh's outReveal);
// taaRenderSize is the render-target size in pixels; taaJitterPx this frame's sub-pixel shear in pixels;
// taaInvViewProjJittered raster NDC -> camera-relative world (this frame); taaPrevViewProj camera-relative
// world -> previous unjittered clip; taaCloudReactive the reactive value a fully covered sky pixel gets.
// 1 = never trust the history there. Partial coverage interpolates towards it from the coverage itself, so
// a wisp at 20% alpha is not treated like a solid cloud bank.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of taa-skymotion (docs/vulkan.md). A fullscreen pass: one draw
// per Use(), so the push block holds only the sampler slot and every other uniform is a record member.
//
// The GLSL 330 source declares all of these inside #if TAAMOTION > 0; the oracle reads the unpreprocessed
// text, so they are names of every variant and are declared unconditionally here. taaCloudReactive's GLSL 330
// initializer (1.0) is seeded by the runtime (docs/vulkan.md).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, transparentRevealTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 taaRenderSize;
    vec2 taaJitterPx;
    mat4 taaInvViewProjJittered;
    mat4 taaPrevViewProj;
    float taaCloudReactive;
};

#if defined(OPTIMUM_VERTEX)


// Optimum TAA (P4): the vertex half of the sky/cloud motion pass.
//
// A fullscreen triangle generated from gl_VertexID, exactly like the other
// Optimum post passes (taa-resolve, fsr-easu), with one difference that is the
// whole point of the pass: gl_Position.z equals gl_Position.w, so the NDC depth
// is 1 and the window depth is 1.0 - the far plane, which is the value Primary's
// depth attachment still holds wherever no geometry was drawn. The depth remap
// at the end keeps that: (1 + 1) * 0.5 = 1 = w.
//
// With the depth test on and GL_LEQUAL the triangle therefore passes on sky
// pixels only (1.0 <= 1.0) and is rejected by every pixel any surface wrote
// depth for (depth < 1.0). That is what keeps this pass from overwriting the
// motion vectors the terrain, entity, liquid and particle writers already put
// down. The pass never writes depth itself.

layout(location = 0) out vec2 texCoord;

void main(void)
{
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
	float y = -1.0 + float((gl_VertexIndex & 2) << 1);
	gl_Position = vec4(x, y, 1.0, 1.0);
	texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


#if TAAMOTION == 1
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#else
// TAA off: the pass never runs (ClientPlatformWindows.RenderOptimumSkyMotion
// returns before binding it), but the program is still registered and compiled,
// and a fragment stage with no output at all is not worth handing to two shader
// translators. One dummy attachment keeps it trivially valid.
layout(location = 0) out vec4 outMotion;
#endif

layout(location = 0) in vec2 texCoord;

#include "motion.glsl"

void main()
{
#if TAAMOTION == 1
	// The transparent layer's coverage of this pixel, which is 1 - revealage -
	// the identical term transparentcompose.fsh composites with. Clouds
	// multiply their own (1 - density) into that attachment through the
	// per-attachment blend factors SystemRenderOITLayers sets, the aurora and
	// the quad particles through oit.fsh's outReveal, so every transparent
	// thing that can sit in front of the sky is in here.
	float coverage = clamp(1.0 - texelFetch(optimumTextures2D[transparentRevealTex], ivec2(gl_FragCoord.xy), 0).r, 0.0, 1.0);
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
	// Behind the previous camera (prevClip.w <= 1e-6) is not a motion vector: the include returns a zero
	// alpha, which routes the pixel back to the resolve's own camera fallback, exactly as in
	// chunkopaque.fsh; the reactive value is still delivered, because taa-resolve.fsh reads motion.b
	// whether or not the pixel was accepted.
	outMotion = optimumWriteMotion(prevClip, taaRenderSize, taaJitterPx, reactive, gl_FragCoord.z);
#else
	outMotion = optimumWriteReactiveOnly(0.0);
#endif
}

#endif
