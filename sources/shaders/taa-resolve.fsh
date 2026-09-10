#version 330 core
// Optimum TAA resolve (P2). One fullscreen pass per frame after all scene
// geometry: reprojects last frame's history by the motion attachment (or by
// camera motion from depth where nothing wrote a vector), rectifies it
// against the current 3x3 neighbourhood in YCoCg, and blends. Writes the new
// history: colour (RGBA16F, alpha = scene alpha), glow (RGBA8) and linear
// view depth (R32F) for next frame's disocclusion test.
//
// Conventions (see TAA-PLAN.md): motion = previousPixel - currentUnjitteredPixel
// in render pixels; a raster pixel centre sits at unjittered position
// centre - jitterPx; history is stored at unjittered pixel centres; the
// motion attachment's alpha is the writer's WINDOW depth in [0,1] (the same
// space as the depth attachment), not NDC depth.

uniform sampler2D sceneTex;      // Primary colour 0, jittered
uniform sampler2D glowTex;       // Primary colour 1, jittered
uniform sampler2D motionTex;     // rg mv px, b reactive, a writerDepth [0,1] (0 = unwritten)
uniform sampler2D depthTex;      // Primary depth, [0,1], 0 = near
uniform sampler2D historyColor;  // previous resolve colour
uniform sampler2D historyGlow;   // previous resolve glow
uniform sampler2D historyDepth;  // previous resolve linear depth

uniform vec2 renderSize;
uniform vec2 jitterPx;           // this frame's raster displacement
uniform mat4 invViewProjJittered;// raster NDC -> camera-relative world (this frame)
uniform mat4 prevViewProj;       // camera-relative world (previous camera) -> previous unjittered clip
uniform mat4 viewMatrix;         // camera-relative world -> view (for linear depth)
uniform vec3 cameraDelta;        // currentCameraPos - previousCameraPos
uniform int resetHistory;
uniform float blendAlpha;        // 0.1 default
uniform float varianceGamma;     // 1.25 default

in vec2 texCoord;

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
	vec4 centreSample = texelFetch(sceneTex, pixel, 0);
	vec4 filtered = vec4(0.0);
	float filteredWeight = 0.0;
	vec3 m1 = vec3(0.0), m2 = vec3(0.0);
	vec3 boxMin = vec3(1e9), boxMax = vec3(-1e9);
	for (int y = -1; y <= 1; y++)
	for (int x = -1; x <= 1; x++)
	{
		ivec2 p = clamp(pixel + ivec2(x, y), ivec2(0), ivec2(renderSize) - ivec2(1));
		vec4 c = texelFetch(sceneTex, p, 0);
		vec3 ycc = rgbToYCoCg(c.rgb);
		m1 += ycc; m2 += ycc * ycc;
		boxMin = min(boxMin, ycc); boxMax = max(boxMax, ycc);
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
	vec3 clipMin = max(boxMin, mu - varianceGamma * sigma);
	vec3 clipMax = min(boxMax, mu + varianceGamma * sigma);

	// ---- depth and linear view depth of this pixel
	float depth = texelFetch(depthTex, pixel, 0).r;
	vec2 ndc = pixelCentre * invSize * 2.0 - 1.0;
	vec4 worldH = invViewProjJittered * vec4(ndc, depth * 2.0 - 1.0, 1.0);
	vec3 world = worldH.xyz / max(abs(worldH.w), 1e-6) * sign(worldH.w);
	float linearDepth = -(viewMatrix * vec4(world, 1.0)).z;

	vec4 glow = texelFetch(glowTex, pixel, 0);

	// ---- motion: written vector when its depth matches, else camera reprojection
	vec4 motion = texelFetch(motionTex, pixel, 0);
	float reactive = clamp(motion.b, 0.0, 1.0);
	vec2 currentUnjittered = pixelCentre - jitterPx;
	vec2 mv;
	// motion.a is the writer's window depth in [0,1], stored in an RGBA16F
	// attachment: half precision alone costs ~5e-4 near 1.0, so the tolerance
	// has to scale with the value and keep a floor for depths near the near
	// plane. A fixed 1e-4 rejected every legitimate writer past mid-range.
	bool written = motion.a > 0.0 && abs(motion.a - depth) <= max(2e-4, 8e-4 * depth);
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
		bool sky = depth >= 0.999999;
		vec4 prevClip = sky ? prevViewProj * vec4(world, 0.0)
		                    : prevViewProj * vec4(world + cameraDelta, 1.0);
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
	if (resetHistory != 0 || offscreen) alpha = 1.0;

	vec4 history = sampleCatmullRom(historyColor, historyUv);
	vec4 historyGlowSample = texture(historyGlow, historyUv);
	float historyLinear = texture(historyDepth, historyUv).r;
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
	}
	// Disocclusion: the surface seen last frame at that location must be at a
	// comparable distance. Tolerance grows with distance; camera translation
	// along the view axis is covered by the relative term. A disoccluded pixel
	// has no valid history at all, so it is rejected outright - half-rejecting
	// it just blends in whatever surface used to be in front.
	float depthTolerance = 0.5 + 0.08 * linearDepth;
	if (abs(historyLinear - linearDepth) > depthTolerance) alpha = 1.0;
	alpha = max(alpha, reactive);

	// ---- rectify and blend in YCoCg with luminance weighting
	float clipKeep = 1.0;
	vec3 histYcc = clipToBox(clipMin, clipMax, rgbToYCoCg(history.rgb), clipKeep);
	vec3 curYcc = rgbToYCoCg(current.rgb);
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
