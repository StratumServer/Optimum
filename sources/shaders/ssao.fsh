#version 330 core

#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
uniform sampler2D gDepth;
#else
uniform sampler2D gPosition;
#endif
uniform sampler2D gNormal;
uniform sampler2D texNoise;
uniform vec3[64] samples;
uniform vec2 screenSize;
uniform sampler2D revealage;

in vec2 texcoord;
out vec4 outOcclusion;

#if SSAOLEVEL == 2
int kernelSize = 24;
float radius = 0.9;
#else
int kernelSize = 20;
float radius = 0.9;
#endif

float bias = 0.01;

uniform mat4 projection;


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
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));

    return vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
vec3 optimumOctDecodeNormal(vec2 e) {
	vec3 v = vec3(e.xy, 1.0 - abs(e.x) - abs(e.y));
	if (v.z < 0.0) {
		v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
	}
	float lenSq = dot(v, v);
	return lenSq > 1e-10 ? v * inversesqrt(lenSq) : vec3(0.0, 0.0, 1.0);
}

float optimumReconstructViewZ(float depth) {
	float ndcZ = depth * 2.0 - 1.0;
	float denom = ndcZ + projection[2][2];
	return abs(denom) > 1e-6 ? -projection[3][2] / denom : -projection[3][2] / -1e-6;
}

vec3 optimumReconstructViewPos(vec2 uv, float depth) {
	float viewZ = optimumReconstructViewZ(depth);
	vec2 ndcXY = uv * 2.0 - 1.0;
	return vec3(
		(ndcXY.x + projection[2][0]) * (-viewZ) / projection[0][0],
		(ndcXY.y + projection[2][1]) * (-viewZ) / projection[1][1],
		viewZ);
}
#endif


void main()
{
#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	vec2 depthTexSize = vec2(textureSize(gDepth, 0));
	vec2 centerUv = (floor(texcoord * depthTexSize) + 0.5) / depthTexSize;
	float centerDepth = texture(gDepth, centerUv).r;
	if (centerDepth >= 1.0) {
		outOcclusion = vec4(1);
		return;
	}

	vec3 fragPos = optimumReconstructViewPos(centerUv, centerDepth);
	float distanceFade = clamp(1.2 - (-fragPos.z) / 250, 0, 1);
	if (distanceFade == 0) {
		outOcclusion = vec4(1);
		return;
	}

	float wboitatn = max(0.0, 1 - texture(revealage, centerUv).r) * 0.75;
	vec4 texVal = texture(gNormal, centerUv);
	float attenuate = texVal.z + wboitatn;
	if (attenuate >= 1.0) {
		outOcclusion = vec4(1);
		return;
	}

	vec3 normal = optimumOctDecodeNormal(texVal.xy);
	bool leavesHack = texVal.w > 0;

	if (!leavesHack) {
		fragPos += normal * clamp(-fragPos.z / 150 - 0.05, 0, 10);
	}
#else
	float wboitatn = max(0.0, 1 - texture(revealage, texcoord).r) * 0.75;
	
	// tile noise texture over screen based on screen dimensions divided by noise size
	vec2 noiseScale = vec2(screenSize.x/8.0, screenSize.y/8.0); 

	vec4 texVal = texture(gPosition, texcoord); 
	
	vec3 fragPos = texVal.xyz;
	float attenuate = texVal.w + wboitatn;
	
	texVal = texture(gNormal, texcoord);
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
#endif

	//vec3 randomVec = texture(texNoise, texcoord * noiseScale).xyz;
	
    const float ditherSize = pow(64.0, 2.0);
    float dither = bayer128(texcoord * screenSize);
	vec3 randomVec = sphereMap(goldenSpiralN(ditherSize + dither, ditherSize));

#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	vec3 tangentDiff = randomVec - normal * dot(randomVec, normal);
    vec3 tangent = dot(tangentDiff, tangentDiff) > 1e-10
		? normalize(tangentDiff)
		: normalize(abs(normal.z) < 0.99 ? vec3(-normal.y, normal.x, 0.0) : vec3(1.0, 0.0, -normal.x));
#else
    vec3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
#endif
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN = mat3(tangent, bitangent, normal);
	
    float occlusion = 0.0;
	
    for( int i = 0; i < kernelSize; ++i)
    {
        vec3 sample = TBN * samples[i];
        sample = fragPos + sample * radius;

        vec4 offset = vec4(sample, 1.0);
        offset = projection * offset;
        offset.xyz /= offset.w;
        offset.xyz = offset.xyz * 0.5 + 0.5;
		
		offset.x = clamp(offset.x, texcoord.x - 0.04,  texcoord.x + 0.04);
		offset.y = clamp(offset.y, texcoord.y - 0.04,  texcoord.y + 0.04);

#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
		if (offset.x <= 0.0 || offset.x >= 1.0 || offset.y <= 0.0 || offset.y >= 1.0) {
			continue;
		}
        float sampleDepth = optimumReconstructViewZ(texture(gDepth, offset.xy).r);
#else
        float sampleDepth = texture(gPosition, offset.xy).z;
#endif
		float depthDiff = sampleDepth - (sample.z + bias);
		float rangeCheck = 0;
		
		if (leavesHack) {

#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
			if (depthDiff >= 0.02 && depthDiff < 0.2 && abs(dot(optimumOctDecodeNormal(texture(gNormal, offset.xy).xy), normal) - 1) > 0.25) {
#else
			if (depthDiff >= 0.02 && depthDiff < 0.2 && abs(dot(texture(gNormal, offset.xy).rgb, normal) - 1) > 0.25) {
#endif
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
	#if SSAOLEVEL == 2
		occ = max(occ, 0.5);
	#else
		occ = max(occ, 0.7);
	#endif
	
	// We need MOAR SSAO >:D
	if (!leavesHack) {
		occ = 1 - (1-occ) * 1.4;
	}
	
	
    outOcclusion = vec4(occ, occ, occ, 1);
}