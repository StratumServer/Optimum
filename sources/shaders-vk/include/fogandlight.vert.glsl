// Native port of the game include fogandlight.vsh (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: fogandlight.vsh
// optimum-port: transformed
// optimum-frame-owner: fogandlight.vsh
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_FOGANDLIGHT_VERT_GLSL
#define OPTIMUM_INCLUDE_FOGANDLIGHT_VERT_GLSL

#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "frame.glsl"
#include "varyings.glsl"
#include "specialization.glsl"
#include "vertexflagbits.glsl"

// DYNLIGHTS, MINBRIGHT and SHADOWQUALITY are the specialization constants OPTIMUM_DYNLIGHTS,
// OPTIMUM_MINBRIGHT and OPTIMUM_SHADOWQUALITY, whose defaults (0) are the #ifndef fallbacks
// this file used to define. The point-light arrays live in the frame block at
// FrameGlobals.MaxDynamicLights; pointLightQuantity still bounds the loop, as it did.
layout(location = OPTIMUM_LOCATION_BLOCK_BRIGHTNESS) out float blockBrightness;

layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) out float glowLevel;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) out vec3 blockLight;

#include "fogspheres.glsl"


vec4 applyLightWithoutPointLight(vec4 sunColor, vec4 blockColor, float bGlow) {
	float bSun = (sunColor.r + sunColor.g + sunColor.b)/3;
	float bBlock = (blockColor.r + blockColor.g + blockColor.b)/3;

	// 1. Mix colors according to their brightness (very bright light has more influence on the color)
	vec4 rgba = (2 * bSun * sunColor + bBlock * blockColor) / (2 * bSun + bBlock);

	// 2. Fix brightness
	rgba *= max(bGlow, max(bSun, bBlock)) / ((rgba.r + rgba.g + rgba.b) / 3);

	if (OPTIMUM_SHADOWQUALITY > 0) {
		blockBrightness = clamp(max(bGlow, bBlock) - bSun/2, 0.0, 1.0);
	}

	// 4. Always fully opaque
	rgba.a = 1;

	return rgba;
}

vec4 getPointLightRgbv(vec3 worldPos, float sunlightIntensity) {
	if (OPTIMUM_DYNLIGHTS == 0) {
		return vec4(0);
	}

	vec4 pointColSum = vec4(0);
	float bPointBrightSum = 0;

	for (int i = 0; i < pointLightQuantity; i++) {
		float lightDistance = length(vec3(worldPos.x - pointLights[i].x, worldPos.y - pointLights[i].y, worldPos.z - pointLights[i].z));
		vec3 plc = pointLightColors[i];
		if (plc.r + plc.g + plc.b < 0.02) continue;

		vec3 color = normalize(plc);
		float range = plc.r / color.r;

		// fugly hack for lightning
		float extra = 1 - sunlightIntensity;
		if (range >= 50) extra = 25;

		range = min(25, range);
		if (lightDistance > range) continue;

		float bright = (color.r + color.g + color.b) / 3;

		float strength = max(0, bright * (max(1 - lightDistance / range, 0) + min(range/15,  1 / lightDistance) * extra));

		pointColSum.w = max(pointColSum.w, strength);
		bPointBrightSum += strength;

		pointColSum.r += color.r * strength;
		pointColSum.g += color.g * strength;
		pointColSum.b += color.b * strength;
	}

	if (bPointBrightSum > 0) {
		pointColSum.rgb /= max(1, bPointBrightSum);
	}

	pointColSum.w /= max(1, glitchStrengthFL * 2);

	return pointColSum;
}


// sunColor = color of the ambient light, or the sun color really
// lightColor = rgb is block light, a is sun light brightness
vec4 applyLight(vec3 ambientColor, vec4 lightColor, int renderFlags, vec4 worldPos) {

	float bGlow = glowLevel = (renderFlags & GlowLevelBitMask) / 256.0;
	float contrast = 1.05;

	vec3 blockLightColor = lightColor.rgb;
	vec3 sunLightColor = max(0.001, lightColor.a) * ambientColor.rgb; // Ugly fix a weird bug where deep ocean gets pitch black at a distance one lightColor.a reaches 0 or negative?

	if (OPTIMUM_DYNLIGHTS == 0) {
		return applyLightWithoutPointLight(vec4(sunLightColor ,1), vec4(blockLightColor,1), bGlow);
	}


	// Sun brightness
	float bSun = (sunLightColor.r + sunLightColor.g + sunLightColor.b)/3;
	// Block brightness
	float bBlock = (blockLightColor.r + blockLightColor.g + blockLightColor.b)/3;

	vec4 pointColSum = getPointLightRgbv(worldPos.xyz, bSun);


	if (nightVisionStrength > 0) {
		pointColSum += vec4(0.1, 0.5, 0.1, 0.45) * nightVisionStrength;
		sunLightColor = mix(sunLightColor, vec3(0.1, 0.5, 0.1), nightVisionStrength);
		bSun += nightVisionStrength;
	}


	// Point light brightness
	float bPoint = pointColSum.w;

	bBlock /= max(1, glitchStrengthFL * 2);

	// Light up all caves
	bBlock = max(OPTIMUM_MINBRIGHT, bBlock);

	bPoint /= max(1, glitchStrengthFL * 2);

	// 1. Mix colors according to their brightness (very bright light has more influence on the color)
	vec3 rgba = (2 * bSun * sunLightColor + bBlock * blockLightColor + bPoint * pointColSum.rgb) / (2 * bSun + bBlock + bPoint);

	// 2. Fix brightness
	float bMax = max(bGlow, max(bPoint, max(bSun, bBlock)));

	blockLight = rgba;

	if (OPTIMUM_SHADOWQUALITY > 0) {
		blockBrightness = clamp(max(bGlow, max(bPoint, bBlock)) - bSun/2, 0.0, 1.0);
	}


	rgba *= bMax / ((rgba.r + rgba.g + rgba.b) / 3);

	rgba *= 1 + bGlow/4;

	rgba *= contrast;

	/*if (nightVisionStrength > 0)
	{
		vec3 nightvision = vec3(
			clamp(rgba.r - 0.5, 0.0, 1.0) * 2,
			clamp(rgba.g - 0.5, 0.0, 1.0) * 1.5,
			clamp(rgba.b - 0.5, 0.0, 1.0) * 2
		);
		rgba.rgb = mix(rgba.rgb, nightvision, nightVisionStrength);
	}*/

	return vec4(rgba, 1);
}



float getFogLevel(vec4 worldPos, float fogMin, float fogDensity) {
	float depth = length(worldPos.xyz);
	float clampedDepth = min(250, depth);
	float heightDiff = worldPos.y - flatFogStart;
	float extraDistanceFog = max(-flatFogDensity * clampedDepth * (flatFogStart) / 60, 0); // div 60 was 160 before, at 160 thick flat fog looks broken when looking at trees
	float distanceFog = 1 - 1 / exp(clampedDepth * fogDensity + extraDistanceFog);

	float flatFog = 1 - 1 / exp(heightDiff * flatFogDensity);

	float val = max(flatFog, distanceFog);
	float nearnessToPlayer = clamp((8-depth)/8, 0.0, 0.9);
	val = max(min(0.04, val), val - nearnessToPlayer);

	// Needs to be added after so that underwater fog still gets applied.
	val += fogMin;

	return clamp(val, 0.0, 1.0);
}



vec4 applyFog(vec4 worldPos, vec4 rgbaPixel, vec4 rgbaFog, float fogMin, float fogDensity) {
	float amount = getFogLevel(worldPos, fogMin, fogDensity);
	vec4 outcolor = vec4(mix(rgbaPixel.rgb, rgbaFog.rgb, amount), rgbaPixel.a * rgbaFog.a);

	outcolor = applySpheresFog(outcolor, amount, worldPos.xyz);

	return outcolor;
}

#endif
