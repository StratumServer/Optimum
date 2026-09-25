// Native port of the game include skycolor.fsh (docs/vulkan.md).
// optimum-port-of: skycolor.fsh
// optimum-port: transformed
// optimum-frame-owner: skycolor.fsh
// optimum-frame-texture: sampler2D glow
// optimum-frame-texture: sampler2D sky
// optimum-program-symbol: vec4 rgbaFog
// optimum-program-symbol: float fogMinIn
// optimum-program-symbol: float fogDensityIn
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_SKYCOLOR_GLSL
#define OPTIMUM_INCLUDE_SKYCOLOR_GLSL

#include "bindings.glsl"
#define OPTIMUM_FRAME_OWNER_SKYCOLOR_FSH
#include "frame.glsl"
#include "specialization.glsl"
#include "dither.glsl"
#include "fogandlight.frag.glsl"

//	<https://www.shadertoy.com/view/4dS3Wd>
//	By Morgan McGuire @morgan3d, http://graphicscodex.com
//
float hash(float n) { return fract(sin(n) * 1e4); }
float hash(vec2 p) { return fract(1e4 * sin(17.0 * p.x + p.y * 0.1) * (0.1 + abs(sin(p.y * 13.0 + p.x)))); }

float noise(float x) {
	float i = floor(x);
	float f = fract(x);
	float u = f * f * (3.0 - 2.0 * f);
	return mix(hash(i), hash(i + 1.0), u);
}

float noise(vec3 x) {
	const vec3 step = vec3(110, 241, 171);

	vec3 i = floor(x);
	vec3 f = fract(x);

	// For performance, compute the base input to a 1D hash from the integer part of the argument and the
	// incremental change to the 1D based on the 3D -> 1D wrapping
    float n = dot(i, step);

	vec3 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix( hash(n + dot(step, vec3(0, 0, 0))), hash(n + dot(step, vec3(1, 0, 0))), u.x),
                   mix( hash(n + dot(step, vec3(0, 1, 0))), hash(n + dot(step, vec3(1, 1, 0))), u.x), u.y),
               mix(mix( hash(n + dot(step, vec3(0, 0, 1))), hash(n + dot(step, vec3(1, 0, 1))), u.x),
                   mix( hash(n + dot(step, vec3(0, 1, 1))), hash(n + dot(step, vec3(1, 1, 1))), u.x), u.y), u.z);
}


float getFogAmountForSky(vec3 skyPosition, vec3 skyPosNorm, float sealevelOffsetFactor, float horizonFog) {
	float fStart = flatFogDensity < 0 ? flatFogStart : 0;
	float earthCurvatureBias = flatFogDensity < 0 ? 0.55 * playerToSealevelOffset : 0; // Add a bit of bias because distant sky has earth curvature?
	float invHorizonDistance = (1 - skyPosNorm.y)/2 + 0.3;

	// Apply fog
	float fogAmount = max(
		fogMinIn + max(fogDensityIn * 120 - 0.12, 0) + max(-flatFogDensity * gl_FragCoord.z * fStart / 3.3, 0),
		(1 - 1 / exp((skyPosition.y - fStart - earthCurvatureBias) * flatFogDensity))
	);

	// Add a little extra fog near the horizon
	float f = 0;

	float rnd = fogWaveCounter / 1.0;
	vec3 rndPos = vec3(rnd + skyPosNorm.x, skyPosNorm.y, skyPosNorm.z - rnd);

	float density = invHorizonDistance * (0.6 + noise(rndPos)/3);

	float fac = max(0.0, density - 0.5) * max(min(1, 5 * fogAmount), horizonFog);
	fogAmount += fac;

	fogAmount = clamp(fogAmount, 0, 1);

	return fogAmount;
}


void getSkyColorAt(vec3 skyPosition, vec3 sunPosition, float sealevelOffsetFactor, float skyLightIntensity, float horizonFog, out vec4 skyColor, out vec4 skyGlow)
{
	vec3 V2 = normalize(vec3(skyPosition.x, skyPosition.y + sealevelOffsetFactor * playerToSealevelOffset, skyPosition.z));

	vec3 V2forGlow = normalize(vec3(skyPosition.x, skyPosition.y, skyPosition.z)); // sealevelOffsetFactor must be 0 for glow

    vec3 skyPosNorm = normalize(V2);
	vec3 skyPosNormForGlow = normalize(V2forGlow);

	vec3 sunPos = sunPosition.xyz;
	vec4 noiseCol = NoiseFromPixelPosition(ivec2(gl_FragCoord.xy), ditherSeed, horizontalResolution);

    // Compute the proximity of this fragment to the sun.

    float vl = 1 - clamp(distance(skyPosNormForGlow, sunPos)/6, 0, 1);
	float invHorizonDistance = pow((1 - skyPosNorm.y), 0.25);  // - 0.2   - this was in there. Caused a weird ring in the sky
	float u = (sunPos.y + 1)/2;
	float v = 1 - vl + noiseCol.y/6;

	// Look up the sky color and glow colors.
	int q=0;
	vec4 Kg = vec4(0);

	int samples = 1;
	for (int dr = -samples; dr <= samples; dr++) {
		Kg += texture(glow, vec2(clamp(u - sunsetMod * 2, 0, 1), v + dr/512.0));
		q++;
	}

	Kg /= q;
    vec4 Ks = texture(sky, vec2(u, invHorizonDistance));

    // Combine the color and glow
	skyColor = vec4(0,0,0,1);
    skyColor.rgb = Ks.rgb * (1 - Kg.a) + Kg.rgb * Kg.a;

	// Apply fog
	float fogAmount =getFogAmountForSky(skyPosition, skyPosNorm, sealevelOffsetFactor, horizonFog);

	skyColor = applyFog(skyColor, fogAmount);
	skyColor = mix(skyColor, vec4(rgbaFog.rgb, fogAmount), 1 - skyLightIntensity);

	skyColor = applySpheresFog(skyColor, fogAmount, skyPosition * 50);

	skyColor += noiseCol;

	if (OPTIMUM_GODRAYS > 0) {
		float intensity = clamp(V2.y/4 + (vl/2 - 0.3) , 0, 0.5);

		skyColor.rgb *= 1 - intensity * 2 * 0.2 * (1 - fogAmount);
		//skyGlow = vec4(0, vl < 0 ? 0 : min(1, vl*vl), 0, 1);
		skyGlow = vec4(0, intensity - fogAmount/2, 0, 1);
	} else {
		skyGlow = vec4(0, 0, 0, 1);
	}
}



vec4 getSkyGlowAt(vec3 skyPosition, vec3 sunPosition, float sealevelOffsetFactor, float skyLightIntensity, float horizonFog, float proximityMul)
{
	vec3 V2 = normalize(vec3(skyPosition.x, skyPosition.y + sealevelOffsetFactor * playerToSealevelOffset, skyPosition.z));

	vec3 V2forGlow = normalize(vec3(skyPosition.x, skyPosition.y, skyPosition.z)); // sealevelOffsetFactor must be 0 for glow

    vec3 skyPosNorm = normalize(V2);
	vec3 skyPosNormForGlow = normalize(V2forGlow);

	vec3 sunPos = sunPosition.xyz;
	vec4 noiseCol = NoiseFromPixelPosition(ivec2(gl_FragCoord.xy), ditherSeed, horizontalResolution);

    // Compute the proximity of this fragment to the sun.

    float vl = 1 - clamp(distance(skyPosNormForGlow, sunPos)/6*proximityMul, 0, 1);
	float invHorizonDistance = pow((1 - skyPosNorm.y), 0.25);  // - 0.2   - this was in there. Caused a weird ring in the sky
	float u = (sunPos.y + 1)/2;
	float v = 1 - vl + noiseCol.y/6;

	// Look up the sky color and glow colors.
	int q=0;
	vec4 Kg = vec4(0);

	int samples = 1;
	for (int dr = -samples; dr <= samples; dr++) {
		Kg += texture(glow, vec2(clamp(u - sunsetMod, 0, 1), v + dr/512.0));
		q++;
	}

	Kg /= q;

	// Apply fog
	float fogAmount =getFogAmountForSky(skyPosition, skyPosNorm, sealevelOffsetFactor, horizonFog);

	Kg = applyFog(Kg, fogAmount);
	Kg = mix(Kg, vec4(rgbaFog.rgb, fogAmount), 1 - skyLightIntensity);

	Kg += noiseCol;

	return Kg;
}

#endif
