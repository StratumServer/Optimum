#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of taa-resolve.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of taa-resolve.fsh (the Optimum program in sources/shaders, docs/vulkan.md).
// The body is the GLSL 330 body token for token; the only differences are the bindless sampler reads
// (texture(optimumTextures2D[name], ...)). The resolve's temporal invariants - 3x3 nearest-depth
// disocclusion with motion from the nearest-depth tap, the closest-tap writer-depth tolerance and the
// luminance anti-flicker 0.3x..1.2x blendAlpha - are reproduced verbatim, with their DO NOT REVERT notes.
//
// Optimum TAA resolve (P2). One fullscreen pass per frame after all scene
// geometry: reprojects last frame's history by the motion attachment (or by
// camera motion from depth where nothing wrote a vector), rectifies it
// against the current 3x3 neighbourhood in YCoCg, and blends. Writes the new
// history: colour (RGBA16F, alpha = scene alpha), glow (RGBA8) and linear
// view depth (R32F) for next frame's disocclusion test.
//
// Conventions (see docs/vulkan.md): motion = previousPixel - currentUnjitteredPixel
// in render pixels; a raster pixel centre sits at unjittered position
// centre - jitterPx; history is stored at unjittered pixel centres; the
// motion attachment's alpha is the writer's WINDOW depth in [0,1] (the same
// space as the depth attachment), not NDC depth.
//
// Samplers (push slots, taa-resolve.interface.glsl):
//   sceneTex      Primary colour 0, jittered
//   glowTex       Primary colour 1, jittered
//   motionTex     rg mv px, b reactive, a writerDepth [0,1] (0 = unwritten)
//   depthTex      Primary depth, [0,1], 0 = near
//   historyColor  previous resolve colour
//   historyGlow   previous resolve glow
//   historyDepth  previous resolve linear depth
// Record: renderSize; jitterPx (this frame's raster displacement); invViewProjJittered (raster NDC ->
// camera-relative world, this frame); prevViewProj (camera-relative world (previous camera) -> previous
// unjittered clip); viewMatrix (camera-relative world -> view, for linear depth); cameraDelta
// (currentCameraPos - previousCameraPos); resetHistory; blendAlpha (0.1 default); varianceGamma (1.25 default).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of taa-resolve (docs/vulkan.md). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in taa-resolve.fsh's declaration order, and every other
// uniform is a record member in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, sceneTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, glowTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, motionTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyColor);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyGlow);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyDepth);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 renderSize;
    vec2 jitterPx;
    mat4 invViewProjJittered;
    mat4 prevViewProj;
    mat4 viewMatrix;
    vec3 cameraDelta;
    int resetHistory;
    float blendAlpha;
    float varianceGamma;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) out vec2 texCoord;

void main(void)
{
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
	float y = -1.0 + float((gl_VertexIndex & 2) << 1);
	gl_Position = vec4(x, y, 0.0, 1.0);
	texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
layout(location = 2) out vec4 outDepth;

vec3 rgbToYCoCg(vec3 c) {
	return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
	            0.5 * c.r - 0.5 * c.b,
	            -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

vec3 yCoCgToRgb(vec3 c) {
	return vec3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// Intersects the history colour with the neighbourhood box (clip, not clamp).
// `keep` reports how much of the history survived the clip: 1 when it was
// already inside the box, 1/maxUnit when it had to be pulled in. The alpha
// channel has no neighbourhood box of its own, so it is rectified toward the
// current alpha by this same factor instead of drifting unchecked.
vec3 clipToBox(vec3 boxMin, vec3 boxMax, vec3 history, out float keep) {
	vec3 centre = 0.5 * (boxMax + boxMin);
	vec3 extent = 0.5 * (boxMax - boxMin) + 1e-5;
	vec3 offset = history - centre;
	vec3 unit = abs(offset / extent);
	float maxUnit = max(unit.x, max(unit.y, unit.z));
	keep = maxUnit > 1.0 ? 1.0 / maxUnit : 1.0;
	return maxUnit > 1.0 ? centre + offset / maxUnit : history;
}

// 9-tap Catmull-Rom on a bilinear sampler (the usual 5-tap optimisation would
// drop corners; keep the full quality for history colour).
vec4 sampleCatmullRom(sampler2D tex, vec2 uv) {
	vec2 samplePos = uv * renderSize;
	vec2 texPos1 = floor(samplePos - 0.5) + 0.5;
	vec2 f = samplePos - texPos1;
	vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
	vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
	vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
	vec2 w3 = f * f * (-0.5 + 0.5 * f);
	vec2 w12 = w1 + w2;
	vec2 offset12 = w2 / w12;
	vec2 texPos0 = (texPos1 - 1.0) / renderSize;
	vec2 texPos3 = (texPos1 + 2.0) / renderSize;
	vec2 texPos12 = (texPos1 + offset12) / renderSize;
	vec4 result = vec4(0.0);
	result += texture(tex, vec2(texPos0.x, texPos0.y)) * w0.x * w0.y;
	result += texture(tex, vec2(texPos12.x, texPos0.y)) * w12.x * w0.y;
	result += texture(tex, vec2(texPos3.x, texPos0.y)) * w3.x * w0.y;
	result += texture(tex, vec2(texPos0.x, texPos12.y)) * w0.x * w12.y;
	result += texture(tex, vec2(texPos12.x, texPos12.y)) * w12.x * w12.y;
	result += texture(tex, vec2(texPos3.x, texPos12.y)) * w3.x * w12.y;
	result += texture(tex, vec2(texPos0.x, texPos3.y)) * w0.x * w3.y;
	result += texture(tex, vec2(texPos12.x, texPos3.y)) * w12.x * w3.y;
	result += texture(tex, vec2(texPos3.x, texPos3.y)) * w3.x * w3.y;
	return max(result, vec4(0.0));
}

float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

void main(void)
{
	vec2 invSize = 1.0 / renderSize;
	ivec2 pixel = ivec2(clamp(texCoord * renderSize, vec2(0.0), renderSize - vec2(1.0)));
	vec2 pixelCentre = vec2(pixel) + 0.5;

	// ---- current frame: 3x3 neighbourhood, un-jittered reconstruction and statistics
	vec4 centreSample = texelFetch(optimumTextures2D[sceneTex], pixel, 0);
	float centreDepth = texelFetch(optimumTextures2D[depthTex], pixel, 0).r;
	float centreLuma = rgbToYCoCg(centreSample.rgb).x;
	// Nearest window depth in the 3x3 (0 = near): its motion and its linear depth
	// drive the reprojection and the disocclusion test, so a sub-pixel leaf in front
	// of a far background keeps one consistent answer across jitter phases.
	float closestDepth = 2.0;
	float farthestDepth = 0.0;
	float neighbourhoodDepth[9];
	ivec2 closestPixel = pixel;
	vec4 filtered = vec4(0.0);
	float filteredWeight = 0.0;
	vec3 m1 = vec3(0.0), m2 = vec3(0.0);
	vec3 boxMin = vec3(1e9), boxMax = vec3(-1e9);
	float fineContrast = 0.0;
	int fineTaps = 0;
	for (int y = -1; y <= 1; y++)
	for (int x = -1; x <= 1; x++)
	{
		ivec2 p = clamp(pixel + ivec2(x, y), ivec2(0), ivec2(renderSize) - ivec2(1));
		vec4 c = texelFetch(optimumTextures2D[sceneTex], p, 0);
		float tapDepth = texelFetch(optimumTextures2D[depthTex], p, 0).r;
		neighbourhoodDepth[(y + 1) * 3 + x + 1] = tapDepth;
		if (tapDepth < closestDepth) { closestDepth = tapDepth; closestPixel = p; }
		farthestDepth = max(farthestDepth, tapDepth);
		vec3 ycc = rgbToYCoCg(c.rgb);
		m1 += ycc; m2 += ycc * ycc;
		boxMin = min(boxMin, ycc); boxMax = max(boxMax, ycc);
		// Only same-surface axial texels count as fine texture. A silhouette's
		// colour jump must not loosen the history clip across its depth edge.
		if (abs(x) + abs(y) == 1 && abs(tapDepth - centreDepth) <= max(2e-4, 8e-4 * centreDepth)) {
			fineContrast += abs(ycc.x - centreLuma);
			fineTaps++;
		}
		// Reconstruct at this pixel's unjittered centre. The tap's raster
		// centre (pixel + (x,y) + 0.5) sits at unjittered position
		// pixelCentre + (x,y) - jitterPx, so its offset from the
		// reconstruction point is (x, y) - jitterPx. Blackman-Harris, radius ~1.
		vec2 d = vec2(x, y) - jitterPx;
		float r = length(d);
		float w = r < 1.0 ? (0.35875 + 0.48829 * cos(3.14159265 * r) + 0.14128 * cos(2.0 * 3.14159265 * r) + 0.01168 * cos(3.0 * 3.14159265 * r)) : 0.0;
		filtered += c * w; filteredWeight += w;
	}
	vec4 current = filteredWeight > 1e-4 ? filtered / filteredWeight : centreSample;
	current = max(current, vec4(0.0));
	vec3 mu = m1 / 9.0;
	vec3 sigma = sqrt(max(m2 / 9.0 - mu * mu, vec3(0.0)));
	// Fine per-texel detail needs a slightly wider variance box to survive
	// alternating jitter samples. Bound the extra width by the observed 3x3
	// range; flat regions and depth edges keep the original clip.
	float textureDetail = fineTaps >= 3
		? clamp((fineContrast / float(fineTaps)) / max(centreLuma, 0.05) * 2.0, 0.0, 1.0)
		: 0.0;
	float localGamma = varianceGamma + 0.25 * textureDetail;
	vec3 clipMin = max(boxMin, mu - localGamma * sigma);
	vec3 clipMax = min(boxMax, mu + localGamma * sigma);

	// ---- depth and linear view depth of this pixel
	float depth = centreDepth;
	vec2 ndc = pixelCentre * invSize * 2.0 - 1.0;
	vec4 worldH = invViewProjJittered * vec4(ndc, depth * 2.0 - 1.0, 1.0);
	vec3 world = worldH.xyz / max(abs(worldH.w), 1e-6) * sign(worldH.w);
	float linearDepth = -(viewMatrix * vec4(world, 1.0)).z;
	vec2 closestCentre = vec2(closestPixel) + 0.5;
	vec2 closestNdc = closestCentre * invSize * 2.0 - 1.0;
	vec4 closestH = invViewProjJittered * vec4(closestNdc, closestDepth * 2.0 - 1.0, 1.0);
	vec3 closestWorld = closestH.xyz / max(abs(closestH.w), 1e-6) * sign(closestH.w);
	float closestLinearDepth = -(viewMatrix * vec4(closestWorld, 1.0)).z;

	vec4 glow = texelFetch(optimumTextures2D[glowTex], pixel, 0);

	// ---- motion: written vector when its depth matches, else camera reprojection
	float reactive = clamp(texelFetch(optimumTextures2D[motionTex], pixel, 0).b, 0.0, 1.0);
	vec4 motion = texelFetch(optimumTextures2D[motionTex], closestPixel, 0);
	vec2 currentUnjittered = closestCentre - jitterPx;
	vec2 mv;
	// motion.a is the writer's window depth in [0,1], stored in an RGBA16F
	// attachment: half precision alone costs ~5e-4 near 1.0, so the tolerance
	// has to scale with the value and keep a floor for depths near the near
	// plane. A fixed 1e-4 rejected every legitimate writer past mid-range.
	bool written = motion.a > 0.0 && abs(motion.a - closestDepth) <= max(2e-4, 8e-4 * closestDepth);
	if (written)
	{
		mv = motion.rg;
	}
	else
	{
		// Sky (depth == 1, nothing wrote depth) is a direction, not a point:
		// reproject it with w = 0 so camera translation cannot move it (plan:
		// "infinite-direction reprojection where depth == 1"). Finite surfaces
		// translate by cameraDelta into the previous camera's frame.
		bool sky = closestDepth >= 0.999999;
		// The sky direction is far point minus near point, never the far point's
		// position alone: the view matrix's eye sits ~1.7 blocks above the origin
		// (CameraMatrixOrigin is a look-at from LocalEyePos), and that offset in a
		// "direction" is a fixed ~0.6 px error at 3000 blocks. Homogeneous
		// difference with the sign of worldH.w * nearH.w, w == 0 counting as
		// positive, exactly as taa-skymotion.fsh does.
		vec4 nearH = invViewProjJittered * vec4(closestNdc, -1.0, 1.0);
		vec3 skyDirection = closestH.xyz * nearH.w - nearH.xyz * closestH.w;
		if ((closestH.w < 0.0) != (nearH.w < 0.0)) skyDirection = -skyDirection;
		vec4 prevClip = sky ? prevViewProj * vec4(skyDirection, 0.0)
		                    : prevViewProj * vec4(closestWorld + cameraDelta, 1.0);
		if (prevClip.w <= 1e-6) { outColor = current; outGlow = glow; outDepth = vec4(linearDepth); return; }
		vec2 prevPixel = (prevClip.xy / prevClip.w * 0.5 + 0.5) * renderSize;
		mv = prevPixel - currentUnjittered;
	}
	// The history grid is the unjittered pixel-centre grid (see the
	// reconstruction kernel above), so the lookup anchor is pixelCentre; mv is
	// a displacement field, and subtracting the jitter here would re-sample the
	// converged history at a different sub-pixel offset every frame - exactly
	// the wobble jitter is supposed to remove.
	vec2 historyUv = (pixelCentre + mv) * invSize;

	// ---- history sample and rejection
	float alpha = blendAlpha;
	bool offscreen = any(lessThan(historyUv, vec2(0.0))) || any(greaterThan(historyUv, vec2(1.0)));
	bool rejected = resetHistory != 0 || offscreen;
	if (rejected) alpha = 1.0;

	vec4 history = sampleCatmullRom(optimumTextures2D[historyColor], historyUv);
	vec4 historyGlowSample = texture(optimumTextures2D[historyGlow], historyUv);
	float historyLinear = texture(optimumTextures2D[historyDepth], historyUv).r;
	// A history slot that was never written (freshly allocated after a
	// framebuffer rebuild) or that caught a division blow-up holds NaN/Inf,
	// and NaN survives any weighted blend, poisoning the pixel forever. Treat
	// it exactly like a reset: this frame's own values, full current weight.
	if (any(isnan(history)) || any(isinf(history))
		|| any(isnan(historyGlowSample)) || any(isinf(historyGlowSample))
		|| isnan(historyLinear) || isinf(historyLinear))
	{
		history = current;
		historyGlowSample = glow;
		historyLinear = linearDepth;
		alpha = 1.0;
		rejected = true;
	}
	// Disocclusion: the surface seen last frame at that location must be at a
	// comparable distance. Tolerance grows with distance; camera translation
	// along the view axis is covered by the relative term. A disoccluded pixel
	// has no valid history at all, so it is rejected outright - half-rejecting
	// it just blends in whatever surface used to be in front.
	// ==== 2026-09-11: distant foliage jitter was THIS test ====================
	// Root cause: a single-sample depth test (this pixel's linear depth against
	// the one history depth under historyUv) rejected history on ~3.7% of distant
	// leaf pixels per frame, on BOTH backends (parity dumps). A sub-pixel leaf
	// covers the leaf in one jitter phase and the far background in the next, so
	// the two depths disagree by tens of blocks and the pixel reset to the raw
	// aliased sample - the shimmer the user saw on distant trees.
	// Fix: the nearest current depth in the 3x3 (closestLinearDepth, the tap the
	// motion vector also comes from) against the nearest finite history depth in
	// the 3x3 around historyUv, tolerance 0.5 + 0.08 * closestLinearDepth. A leaf
	// that moves one pixel between phases stays inside both windows and keeps its
	// history. Measured: leaf-far rejection ~3.7% -> ~1.1% per frame; the user
	// confirmed on Vulkan that the distant-foliage flicker is gone.
	// Guard: scripts/dev/taa-rejection.py on a parity dump (3x3 leaf-far <= 1.5%).
	// DO NOT REVERT to a single-sample depth test. Pinned by
	// TaaResolveTests.FlippingSubPixelLeafKeepsItsHistory,
	// TaaResolveTests.DisocclusionLargerThanTheNeighbourhoodStillResets,
	// TaaResolveTests.MotionComesFromTheNearestDepthTapAtAnEdge and
	// Optimum.Tests TaaAntiFlickerCoverageTests.
	// ==========================================================================
	// Nearest history depth in the 3x3 around the reprojected point, against the
	// nearest current depth: a single-sample test flips on sub-pixel foliage every
	// few frames (leaf in one jitter phase, background in the next) and threw the
	// history away on ~3.7% of distant leaf pixels per frame.
	float historyNearest = historyLinear;
	for (int hy = -1; hy <= 1; hy++)
	for (int hx = -1; hx <= 1; hx++)
	{
		float h = texture(optimumTextures2D[historyDepth], historyUv + vec2(hx, hy) * invSize).r;
		if (!isnan(h) && !isinf(h)) historyNearest = min(historyNearest, h);
	}
	float depthTolerance = 0.5 + 0.08 * closestLinearDepth;
	// Keep clipped history only for sparse distant coverage, such as a subpixel
	// leaf. A solid silhouette has three or more foreground taps and must reset
	// when its old depth no longer matches; otherwise it leaves a smear trail.
	int nearDepthTaps = 0;
	for (int i = 0; i < 9; i++)
		if (abs(neighbourhoodDepth[i] - closestDepth) <= 2e-4) nearDepthTaps++;
	bool distantDepthEdge = closestLinearDepth > 20.0 &&
		farthestDepth - closestDepth > 2e-4 && nearDepthTaps <= 2;
	bool depthMismatch = abs(historyNearest - closestLinearDepth) > depthTolerance;
	if (depthMismatch && !distantDepthEdge)
	{ alpha = 1.0; rejected = true; }
	else if (depthMismatch) historyGlowSample = glow;

	// ---- rectify and blend in YCoCg with luminance weighting
	float clipKeep = 1.0;
	vec3 histYcc = clipToBox(clipMin, clipMax, rgbToYCoCg(history.rgb), clipKeep);
	vec3 curYcc = rgbToYCoCg(current.rgb);
	// ==== 2026-09-11: distant foliage jitter, second half =====================
	// Root cause: with a fixed current weight (blendAlpha for every pixel that
	// survived rejection), a sub-pixel leaf that enters and leaves the 3x3 moves
	// the neighbourhood clip box every frame, and the clip drags the history
	// with it at full blendAlpha - the history itself oscillates.
	// Fix: current weight mix(1.2, 0.3, w * w) * blendAlpha with
	// w = 1 - |lumCur - lumHist| / max(lumCur, max(lumHist, 0.2)) on the
	// rectified YCoCg luminance, only for pixels not rejected above (reset,
	// off-screen, NaN history, disocclusion keep alpha = 1); reactive is applied
	// after it. Together with the 3x3 nearest-depth test above this took distant
	// leaf rejection from ~3.7% to ~1.1% per frame and removed the flicker in game.
	// DO NOT REVERT to a fixed blend weight. Pinned by
	// TaaResolveTests.AntiFlickerWeightsFollowTheLuminanceDifference and
	// Optimum.Tests TaaAntiFlickerCoverageTests.
	// ==========================================================================
	// Anti-flicker feedback (Playdead INSIDE TAA, 2016): a sub-pixel feature that
	// appears in some jitter phases and not in others moves the neighbourhood box
	// every frame, and a fixed current weight lets the clip drag the history back
	// and forth - the shimmer on distant foliage. Weight the current sample by how
	// different it is from the rectified history in luminance: near-identical
	// pixels keep more history (0.3 x blendAlpha), real changes take more of the
	// current frame (1.2 x blendAlpha). Rejected pixels keep their full reset.
	if (!rejected)
	{
		float lumCur = max(curYcc.x, 0.0);
		float lumHist = max(histYcc.x, 0.0);
		float unbiasedDiff = abs(lumCur - lumHist) / max(lumCur, max(lumHist, 0.2));
		float unbiasedWeight = 1.0 - unbiasedDiff;
		alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, unbiasedWeight * unbiasedWeight);
	}
	alpha = max(alpha, reactive);
	float wCur = alpha / (1.0 + curYcc.x);
	float wHist = (1.0 - alpha) / (1.0 + histYcc.x);
	vec3 resolvedYcc = (curYcc * wCur + histYcc * wHist) / max(wCur + wHist, 1e-5);
	vec3 resolved = max(yCoCgToRgb(resolvedYcc), vec3(0.0));
	// Rectify the history alpha by the same factor the colour clip applied,
	// then blend it with the same weight, so scene alpha cannot drift away
	// from the colour it belongs to.
	float histAlpha = mix(current.a, history.a, clipKeep);
	float resolvedAlpha = mix(histAlpha, current.a, alpha);

	// Glow blends with the same alpha as colour: a separate 0.2 floor made the
	// two signals converge at different rates, so bloom lagged or led the image
	// it is derived from.
	vec4 resolvedGlow = mix(historyGlowSample, glow, alpha);

	outColor = vec4(resolved, resolvedAlpha);
	outGlow = resolvedGlow;
	outDepth = vec4(linearDepth);
}

#endif
