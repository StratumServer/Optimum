// Native port of the game include fogandlight.fsh (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: fogandlight.fsh
// optimum-port: transformed
// optimum-frame-owner: fogandlight.fsh
// optimum-frame-texture: sampler2DShadow shadowMapFar
// optimum-frame-texture: sampler2DShadow shadowMapNear
// optimum-program-uniform: float flatFogDensity
// optimum-program-uniform: float flatFogStart
// optimum-program-uniform: float viewDistance
// optimum-program-uniform: float viewDistanceLod0
// optimum-program-uniform: float windWaveCounter
// optimum-program-symbol: vec4 rgbaFog
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_FOGANDLIGHT_FRAG_GLSL
#define OPTIMUM_INCLUDE_FOGANDLIGHT_FRAG_GLSL

#include "bindings.glsl"
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_FSH
#include "frame.glsl"
#include "varyings.glsl"
#include "specialization.glsl"

// SHADOWQUALITY is the specialization constant OPTIMUM_SHADOWQUALITY: these inputs are
// declared unconditionally, and getBrightnessFromShadowMap branches on the constant.
layout(location = OPTIMUM_LOCATION_BLOCK_BRIGHTNESS) in float blockBrightness;
layout(location = OPTIMUM_LOCATION_SHADOW_COORDS_FAR) in vec4 shadowCoordsFar;
layout(location = OPTIMUM_LOCATION_SHADOW_COORDS_NEAR) in vec4 shadowCoordsNear;

const float Epsilon = 0.001;



#include "noise3d.glsl"
#include "vertexflagbits.glsl"
#include "fogspheres.glsl"

vec4 applyFrostEffect(float frostAlpha, vec4 texColor, vec3 normal, vec3 noisepos) {
	if (frostAlpha > 0) {
		noisepos = round(noisepos * 32.0) / 32;
		noisepos.xyz *= 1.5;

		frostAlpha*=1+max(0.0, normal.y/3);
		frostAlpha *= (valuenoise(noisepos * 2) + valuenoise(noisepos * 12)) * 1.25 - 0.25;

		float heretemp = -10;
		float w = clamp((0.333 - heretemp) * 15, 0, 1);

		vec3 frostColor = vec3(1);
		float faw = frostAlpha * w;
		texColor.rgb = texColor.rgb * (1 - faw) + frostColor * faw;
	}

	return texColor;
}

vec3 palette( float t ){
     vec3 a = vec3((-sin(windWaveCounter/15.0*1.32456)), cos(1/25.0*0.76354), sin(windWaveCounter/14.5));
     vec3 b = vec3(.75,.25,.65);
     vec3 c = vec3(1.,1.,1.);
     vec3 d = vec3(0.263,0.416,0.557);
     return a*b-tan( 6.28318*(c*t+d) );
}

vec4 applyPsychedelicEffect(vec4 texColor, vec3 rustVec, int sub) {
	if (texColor.a <= 0) return texColor;

	float df = clamp((gl_FragCoord.w*20 - 0.3) * 20, 0.09, 1);

	vec3 uv = rustVec;

    vec3 uv0 = uv;
    vec3 fcol = vec3(-0.01, -0.01, -0.01);
    float f = max(5, 15.0 * clamp((df + 0.2)/3.0, 0, 1));

	float t = windWaveCounter / 15.5;

    for (float i =1.0; i<f; i++)
	{
		float luv = length(uv);
        uv.x += sin(uv.y*i-luv+t)/i;
        uv.y += sin(uv.x*i+luv+t)/i;
		uv.z += sin(uv.z*i+luv+t)/i;

        float d = luv*exp(-length(uv0));

        d = 1/8.0;
        d = pow(0.01+d,1.2);

        vec3 col = cos(palette(luv+i*0.4));

        fcol += col*(d + max(0, 0.1-df))/(f/i);
    }

	if (sub > 0) fcol = -2*fcol;

	float b = min(1, (texColor.r+texColor.g+texColor.b));

    vec3 outcolor = mix(texColor.rgb, texColor.rgb + b*fcol.rgb, psychedelicStrength);

	return vec4(outcolor.r, outcolor.g, outcolor.b, texColor.a);
}

vec4 applyRustEffect(vec4 texColor, vec3 normal, vec3 rustVec, int spotty) {
		float f = clamp(gl_FragCoord.w*3 - 0.3, 0, 1);
		if (f <= 0) return texColor;

		float b = clamp(((texColor.r + texColor.g + texColor.b) / 3.0 ) * 10, 0, 1);
		float intensity = b * glitchStrength;
		if (spotty > 0) intensity *= max(0.0, cnoise(vec3(rustVec.x * 0.35, rustVec.y * 0.35, rustVec.z * 0.35)) + 0.3);
		else intensity *= 1.3 * max(0.0, cnoise(vec3(rustVec.x * 1.5, rustVec.y * 1.5, rustVec.z * 0.35)) + 0.35);

		if (intensity < 0.01) return texColor;

		float uvx = round(rustVec.x * 32.0) / 100.0 / 32;
		float uvy = round(rustVec.y * 32.0) / 100.0 / 32;
		float uvz = round(rustVec.z * 32.0) / 100.0 / 32;

		if (normal.y < 0.5) {
			float val = 0.2 * cnoise(vec3(uvx*3000, uvy*700 + windWaveCounter * 1.5, uvz*3000 + windWaveCounter / 5)) + 0.1 * cnoise(vec3(uvx*15000, uvy*3500 + windWaveCounter, uvz*15000 + windWaveCounter / 5));
			texColor.rgb += intensity * val;
		} else {
			float val = 0.2 * cnoise(vec3(uvx*700, uvy*700 + windWaveCounter / 2, uvz*700)) + 0.1 * cnoise(vec3(uvx*15000, uvy*3500 + windWaveCounter / 5, uvz*15000));
			texColor.rgb += intensity * val;
		}

		return texColor;
}

vec4 applyReflectiveEffect(vec4 texColor, inout float glow, int renderFlags, vec2 uv, vec3 normal, vec4 worldPos, vec4 camPos, vec3 blockLight) {
	if ((renderFlags & ReflectiveBitMask) == 0) return texColor;

	// We use the wind data bits as the reflective mode
	// This unfortunately means we can't have something reflective *and* wind affected
	int windMode = (renderFlags >> 29) & 0x7;

	if (windMode == ReflectiveModeWeak) {
		vec3 worldVec = normalize(worldPos.xyz);

		float uvx = round(uv.x * 64 * 32 * 1.0) * 4.0 / 32;
		float uvy = round(uv.y * 64 * 32 * 1.0) * 4.0 / 32;
		float uvz = round(1.0 * 32) * 8.0 / 32;

		float fd = 1 * (gnoise(vec3(uvx, uvy, uvz)));
		fd *= 25*gnoise(round(worldPos.xyz * 20.0) / 30);
		fd = max(0.0,fd + 1);
		float nb = max(0.1, 0.5 * dot(normal, lightPosition));

		texColor.rgb*= 1.0 +vec3(nb * fd) / 2.0;
		texColor.a = clamp(texColor.a + nb*fd, texColor.a/2, 1);
		glow+=nb*fd * 0.15;

		return texColor;
	}

	if (windMode == ReflectiveModeMedium) {
		vec3 worldVec = normalize(worldPos.xyz);

		float uvx = round(uv.x * 64 * 32 * 1.0) * 8.0 / 32;
		float uvy = round(uv.y * 64 * 32 * 1.0) * 8.0 / 32;
		float uvz = round(1 * 32.0) * 8.0 / 32;

		float fd = 1 * (gnoise(vec3(uvx, uvy, uvz)));
		fd *= 15*gnoise(round(worldPos.xyz * 30.0) / 30);
		fd = max(0.0,fd + 1);
		float nb = max(0.1, 0.5 * dot(normal, lightPosition));

		if (windMode == ReflectiveModeMild) fd/=3;

		texColor.rgb*= 1.0 +vec3(nb * fd) / 2.0;
		glow+=nb*fd * 0.15;

		return texColor;
	}

	if (windMode == ReflectiveModeStrong || windMode == ReflectiveModeMild) {
		vec3 worldVec = normalize(worldPos.xyz);

		float uvx = round(uv.x * 64 * 32 * 1.0) * 8.0 / 32;
		float uvy = round(uv.y * 64 * 32 * 1.0) * 8.0 / 32;
		float uvz = round(1 * 32.0) * 8.0 / 32;

		float fd = 1 * (gnoise(vec3(uvx, uvy, uvz)));
		fd *= 25*gnoise(round(worldPos.xyz * 100.0) / 30);
		fd = max(0.0,fd + 1);
		float nb = max(0.1, 0.5 * dot(normal, lightPosition));
		texColor.rgb*= 1.4 +vec3(nb * fd) / 2.0;
		glow+=nb*fd * 0.15;

		return texColor;
	}

	if (windMode == ReflectiveModeSparkly) {

		vec3 worldVec = normalize(worldPos.xyz);
		float mul=3;
		float uvx = round(uv.x * 64 * 32 * 2.0) * 8.0 / 32;
		float uvy = round(uv.y * 64 * 32 * 2.0) * 8.0 / 32;

		float fd = 1 * (gnoise(vec3(uvx, uvy, 0)));
		fd *= 50*gnoise(round(camPos.xyz * 150.0) / 30);
		fd = max(0.0,fd + 1);
		float nb = max(0.1, 0.5 * dot(normal, lightPosition));
		texColor.rgb*=1+vec3(nb * fd) / 2.0;
		glow+=nb*fd * 0.03;

		return texColor;
	}

	if (windMode==5) {
		texColor.rgb = vec3(1);
	}

	return texColor;
}


float linearDepth(float depthSample)
{
    depthSample = 2.0 * depthSample - 1.0;
    float zLinear = 2.0 * zNear / (zFar + zNear - depthSample * (zFar - zNear));
    return zLinear;
}

// result suitable for assigning to gl_FragDepth
float depthSample(float linearDepth)
{
    float nonLinearDepth = (zFar + zNear - 2.0 * zNear * zFar / linearDepth) / (zFar - zNear);
    nonLinearDepth = (nonLinearDepth + 1.0) / 2.0;
    return nonLinearDepth;
}




vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
	return vec4(mix(rgbaPixel.rgb, rgbaFog.rgb, fogWeight), rgbaPixel.a);
}


float getBrightnessFromShadowMap() {
	// b was declared inside the SHADOWQUALITY > 0 block; the > 1 block only ever ran
	// after it, so declaring it up front changes no value that is read.
	float b = 1.0;
	if (OPTIMUM_SHADOWQUALITY > 0) {
		float totalFar = 9.0;
		if (shadowCoordsFar.w > 0) {
			for (int x = -1; x <= 1; x++) {
				for (int y = -1; y <= 1; y++) {
					totalFar -= texture (shadowMapFar, vec3(shadowCoordsFar.xy + vec2(x * shadowMapWidthInv, y * shadowMapHeightInv), shadowCoordsFar.z - 0.0009));
				}
			}
		}
		totalFar /= 9.0;


		b = 1.0 - shadowIntensity * totalFar * shadowCoordsFar.w * 0.5;
	}


	if (OPTIMUM_SHADOWQUALITY > 1) {
		float totalNear = 9.0;
		if (shadowCoordsNear.w > 0) {
			for (int x = -1; x <= 1; x++) {
				for (int y = -1; y <= 1; y++) {
					totalNear -= texture (shadowMapNear, vec3(shadowCoordsNear.xy + vec2(x * shadowMapWidthInv, y * shadowMapHeightInv), shadowCoordsNear.z - 0.0005));
				}
			}
		}

		totalNear /= 9.0;




		b -=  shadowIntensity * totalNear * shadowCoordsNear.w * 0.5;
	}

	if (OPTIMUM_SHADOWQUALITY > 0) {
		b = clamp(b + blockBrightness, 0, 1);
		return b;
	}

	return 1.0;
}


float getBrightnessFromNormal(vec3 normal, float normalShadeIntensity, float minNormalShade) {

	// Option 2: Completely hides peter panning, but makes semi sunfacing block sides pretty dark
	float nb = max(minNormalShade, 0.5 + 0.5 * dot(normal, lightPosition));

	// Let's also define that diffuse light from the sky provides an additional brightness boost for up facing stuff
	// because the top side of blocks being darker than the sides is uncanny o__O
	nb = max(nb, normal.y * 0.95);

	return mix(1, nb, normalShadeIntensity);
}


vec4 applyFogAndShadow(vec4 rgbaPixel, float fogWeight) {
	float b = getBrightnessFromShadowMap();
	rgbaPixel *= vec4(b, b, b, 1);

	return applyFog(rgbaPixel, fogWeight);
}

vec4 applyFogAndShadowWithNormal(vec4 rgbaPixel, float fogAmount, vec3 normal, float normalShadeIntensity, float minNormalShade, vec3 worldPos) {
	float b = getBrightnessFromShadowMap();
	float nb = getBrightnessFromNormal(normal, normalShadeIntensity, minNormalShade);

	b = min(b, nb);
	b *= 1+max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;

	rgbaPixel *= vec4(b, b, b, 1);

	vec4 outcolor = applyFog(rgbaPixel, fogAmount);
	outcolor = applySpheresFog(outcolor, fogAmount, worldPos);
	return outcolor;
}

vec4 applyFogAndShadowFromBrightness(vec4 rgbaPixel, float fogAmount, float b, vec3 worldPos) {
	b *= 1+max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;

	rgbaPixel *= vec4(b, b, b, 1);

	vec4 outcolor = applyFog(rgbaPixel, fogAmount);
	outcolor = applySpheresFog(outcolor, fogAmount, worldPos);

	return outcolor;
}


float getFogLevel(float fogMin, float fogDensity, float worldPosY) {
	float depth = gl_FragCoord.z;
	float clampedDepth = min(250, depth);
	float heightDiff = worldPosY - flatFogStart;

	//float extraDistanceFog = max(-flatFogDensity * flatFogStart / (160 + heightDiff * 3), 0);   // heightDiff*3 seems to fix distant mountains being supper fogged on most flat fog values
	// ^ this breaks stuff. Also doesn't seem to be needed? Seems to work fine without

	float extraDistanceFog = max(-flatFogDensity * clampedDepth * (flatFogStart) / 60, 0); // div 60 was 160 before, at 160 thick flat fog looks broken when looking at trees

	float distanceFog = 1 - 1 / exp(clampedDepth * (fogDensity + extraDistanceFog));
	float flatFog = 1 - 1 / exp(heightDiff * flatFogDensity);

	float val = max(flatFog, distanceFog);
	float nearnessToPlayer = clamp((8-depth)/8, 0, 0.9);
	val = max(min(0.04, val), val - nearnessToPlayer);

	// Needs to be added after so that underwater fog still gets applied.
	val += fogMin;

	return clamp(val, 0, 1);
}

#endif
