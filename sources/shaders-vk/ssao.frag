#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of ssao.fsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// SSAOLEVEL == 2 is a specialization-constant branch with the same values. TAAMOTION stays a variant axis
// (section 5). The loop local `sample` is `samplePos`: `sample` is a reserved word in GLSL 450.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "ssao.interface.glsl"

layout(location = 0) in vec2 texcoord;
layout(location = 0) out vec4 outOcclusion;

int kernelSize = (OPTIMUM_SSAOLEVEL == 2) ? 24 : 20;
float radius = 0.9;

float bias = 0.01;


// Useful numbers
#define PI  radians(180.0)
#define TAU PI * 2.0
#define RCPPI 1.0 / PI
#define PHI sqrt(5.0) * 0.5 + 0.5
#define GOLDEN_ANGLE TAU / PHI / PHI
#define LOG2 log(2.0)

#define cubicSmooth(x) (x * x) * (3.0 - 2.0 * x)


float bayer2(vec2 a){
    a = floor(a);
    return fract(dot(a, vec2(0.5, a.y * 0.75)));
}

float bayer4(vec2 a)   { return bayer2( 0.5  *a) * 0.25     + bayer2(a); }
float bayer8(vec2 a)   { return bayer4( 0.5  *a) * 0.25     + bayer2(a); }
float bayer16(vec2 a)  { return bayer4( 0.25 *a) * 0.0625   + bayer4(a); }
float bayer32(vec2 a)  { return bayer8( 0.25 *a) * 0.0625   + bayer4(a); }
float bayer64(vec2 a)  { return bayer8( 0.125*a) * 0.015625 + bayer8(a); }
float bayer128(vec2 a) { return bayer16(0.125*a) * 0.015625 + bayer8(a); }

// Fermats golden spiral, input a dither pattern as the index and its size as the total to generate coordinates following the spiral.
vec2 goldenSpiralN(float index, float total) {
	float theta = index * GOLDEN_ANGLE;
	return vec2(sin(theta), cos(theta)) * sqrt(index / total);
}

vec2 goldenSpiralS(float index, float total) {
	float theta = index * GOLDEN_ANGLE;
	return vec2(sin(theta), cos(theta)) * pow(index / total, 2.0);
}

// Useful tool to convert 2D offset patterns into 3D. Looks great with screen space stuff, more complicated things such as path tracing should use a rand.
vec3 sphereMap(vec2 a) {
    float phi = a.y * 2.0 * PI;
    float cosTheta = 1.0 - a.x;
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    return vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}


void main()
{
	float wboitatn = max(0.0, 1 - texture(optimumTextures2D[revealage], texcoord).r) * 0.75;

	// tile noise texture over screen based on screen dimensions divided by noise size
	vec2 noiseScale = vec2(screenSize.x/8.0, screenSize.y/8.0);

	vec4 texVal = texture(optimumTextures2D[gPosition], texcoord);

	vec3 fragPos = texVal.xyz;
	float attenuate = texVal.w + wboitatn;

	texVal = texture(optimumTextures2D[gNormal], texcoord);
	vec3 normal = normalize(texVal.xyz);
	bool leavesHack = texVal.w > 0;

	// This seems to completely fix any distant ssao flickering artifacts while perservering everything else
	// Tyron Mar 9: Completely borks fragments behind leaves, during heavy rain
	// Tyron Mar10: Breaks distant cliff walls, changed 90 to 150
	if (!leavesHack) {
		fragPos += normal * clamp(-fragPos.z/150 - 0.05, 0, 10);
	}


	float distanceFade = clamp(1.2 - (-fragPos.z) / 250, 0, 1);

	if (fragPos.x == 0 || distanceFade == 0) {
		outOcclusion = vec4(1);
		return;
	}

	//vec3 randomVec = texture(texNoise, texcoord * noiseScale).xyz;

    const float ditherSize = pow(64.0, 2.0);
    float dither = bayer128(texcoord * screenSize);
#if TAAMOTION == 1
	// Give that screen-locked dither a temporal dimension. Vanilla's Bayer-128
	// is fixed to the pixel grid, so under a jittered camera every surface point
	// draws a different kernel every frame and nothing can average it. Advancing
	// the dither by the golden ratio per frame makes successive frames sample
	// complementary spiral directions instead, which is what a temporal
	// accumulator converges on. Gated on TAAMOTION (stamped from
	// OptimumConfig.EffectiveTemporalPipeline): with no accumulator behind it a
	// per-frame-varying dither is strictly worse than the fixed one.
	dither = fract(dither + fract(temporalFrameIndex * (PHI - 1.0)));
#endif
	vec3 randomVec = sphereMap(goldenSpiralN(ditherSize + dither, ditherSize));


    vec3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN = mat3(tangent, bitangent, normal);

    float occlusion = 0.0;

    for( int i = 0; i < kernelSize; ++i)
    {
        vec3 samplePos = TBN * samples[i];
        samplePos = fragPos + samplePos * radius;

        vec4 offset = vec4(samplePos, 1.0);
        offset = projection * offset;
        offset.xyz /= offset.w;
        offset.xyz = offset.xyz * 0.5 + 0.5;

		offset.x = clamp(offset.x, texcoord.x - 0.04,  texcoord.x + 0.04);
		offset.y = clamp(offset.y, texcoord.y - 0.04,  texcoord.y + 0.04);

        float sampleDepth = texture(optimumTextures2D[gPosition], offset.xy).z;
		float depthDiff = sampleDepth - (samplePos.z + bias);
		float rangeCheck = 0;

		if (leavesHack) {

			if (depthDiff >= 0.02 && depthDiff < 0.2 && abs(dot(texture(optimumTextures2D[gNormal], offset.xy).rgb, normal) - 1) > 0.25) {
				rangeCheck = smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));
			}

		} else {

			if (depthDiff > 0 && depthDiff < 0.2) {
				rangeCheck = smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));
			}

		}

		occlusion += rangeCheck;
    }

	float occ = clamp(1.0 - min(1, occlusion / kernelSize * distanceFade) * (1-attenuate), 0, 1);

	// Some distant geometry gets overly dark, lets clamp the lower limit
	if (OPTIMUM_SSAOLEVEL == 2) {
		occ = max(occ, 0.5);
	} else {
		occ = max(occ, 0.7);
	}

	// We need MOAR SSAO >:D
	if (!leavesHack) {
		occ = 1 - (1-occ) * 1.4;
	}


    outOcclusion = vec4(occ, occ, occ, 1);
}
