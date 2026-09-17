// Shared declarations of Optimum's ambient occlusion compute passes
// (docs/research/ambient-occlusion.md, section C): the push constant block, the
// specialization constant ids, depth and position reconstruction, the edge
// packing and the Hilbert + R2 noise.
//
// Ported from XeGTAO (XeGTAO.hlsli, XeGTAO.h, vaGTAO.hlsl):
//
//   Copyright (C) 2016-2021, Intel Corporation
//   SPDX-License-Identifier: MIT
//   https://github.com/GameTechDev/XeGTAO
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy
//   of this software and associated documentation files (the "Software"), to deal
//   in the Software without restriction, including without limitation the rights
//   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//   copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in all
//   copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//   SOFTWARE.
//
// These passes bind only their own pass set (set 0 of a compute program) and touch
// none of the shared sets of include/bindings.glsl.
//
// The loader (AmbientOcclusion/GtaoShaderSources.cs) expands the #include lines and
// defines GTAO_DEPTH_FORMAT and GTAO_TERM_FORMAT after #version: the storage format
// qualifiers of the formats the device actually chose (r32f or rgba32f, r8 or rgba8),
// so a fallback format never disagrees with its qualifier.

#ifndef OPTIMUM_GTAO_COMMON_GLSL
#define OPTIMUM_GTAO_COMMON_GLSL

#define GTAO_PI 3.1415926535897932384626433832795
#define GTAO_PI_HALF 1.5707963267948966192313216916398

// Working depth levels: mip 0 at render resolution and four halvings (C.1).
#define GTAO_DEPTH_MIP_LEVELS 5.0

// The pre-denoise term is stored as visibility / 1.5: "raw, pre-denoised occlusion
// term can overshoot 1 but will later average out to 1" (XeGTAO.h). The last denoise
// pass scales it back.
#define GTAO_OCCLUSION_TERM_SCALE 1.5

// Specialization constant ids; AmbientOcclusion/GtaoSettings.cs (GtaoSpecialization)
// mirrors them and a test keeps the two in agreement.
#define GTAO_SPEC_INTEGRATION 0
#define GTAO_SPEC_SLICE_COUNT 1
#define GTAO_SPEC_STEPS_PER_SLICE 2
#define GTAO_SPEC_THICKNESS 3
#define GTAO_SPEC_CLASS_CHANNEL 4
#define GTAO_SPEC_NOISE_CYCLE 5
#define GTAO_SPEC_NORMAL_EDGES 6
#define GTAO_SPEC_FINAL_APPLY 7

// INTEGRATION (C.3, C.13).
#define GTAO_INTEGRATION_BITMASK_COS 0u
#define GTAO_INTEGRATION_BITMASK_UNIFORM 1u
#define GTAO_INTEGRATION_HORIZON 2u

// THICKNESS (C.5, C.13). WIDTH is not implemented (see the design's C.5).
#define GTAO_THICKNESS_CONST 0u
#define GTAO_THICKNESS_DIST 1u
#define GTAO_THICKNESS_RANDOM 2u

// 32 sectors in one uint (C.3).
#define GTAO_SECTORS 32u

// 80 bytes, every member 4 bytes wide so the offsets are the declaration order
// times four (GtaoSettings.PushConstants packs them in the same order).
layout(push_constant) uniform GtaoConstants
{
    float depthUnpackMul;          //  0: viewZ = depthUnpackMul / (depthUnpackAdd - depth)
    float depthUnpackAdd;          //  4
    float ndcToViewMulX;           //  8: view.xy = (mul * uv + add) * viewZ, uv row 0 at the bottom (GL order)
    float ndcToViewMulY;           // 12
    float ndcToViewAddX;           // 16
    float ndcToViewAddY;           // 20
    float effectRadius;            // 24: blocks
    float effectFalloffRange;      // 28
    float radiusMultiplier;        // 32
    float finalValuePower;         // 36: measurement only, 1.0 (C.11)
    float sampleDistributionPower; // 40
    float depthMipSamplingOffset;  // 44
    float thickness;               // 48: blocks, solid surfaces (C.5)
    float thicknessThin;           // 52: blocks, the thin class
    float thicknessDistanceScale;  // 56: per block of view distance
    float farFadeBias;             // 60: fade = clamp(bias - viewZ * scale, 0, 1) (C.6)
    float farFadeScale;            // 64
    uint noiseIndex;               // 68: FrameIndex while TAA runs, else 0 (C.7)
    float denoiseBlurBeta;         // 72
    uint reserved;                 // 76
} gtao;

float gtaoViewDepth(float screenDepth)
{
    // GL depth [0, 1] of a GL projection: DepthUnpackConsts = (-B/2, (1-A)/2), A = P[2][2], B = P[3][2].
    float depth = gtao.depthUnpackMul / (gtao.depthUnpackAdd - screenDepth);
    return clamp(depth, 0.0, 3.402823466e+38);
}

vec3 gtaoViewPosition(vec2 screenPos, float viewspaceDepth)
{
    // The working frame: x right, y up, z forward (GL view space mirrored in z).
    return vec3((vec2(gtao.ndcToViewMulX, gtao.ndcToViewMulY) * screenPos +
                 vec2(gtao.ndcToViewAddX, gtao.ndcToViewAddY)) * viewspaceDepth, viewspaceDepth);
}

// [Drobot2014a] Low Level Optimizations for GCN (XeGTAO_FastSqrt).
float gtaoFastSqrt(float x)
{
    return intBitsToFloat(0x1fbd1df5 + (floatBitsToInt(x) >> 1));
}

// Input [-1, 1], output [0, PI] (XeGTAO_FastACos); the input is clamped so a
// dot product a hair above 1 cannot feed a negative number to gtaoFastSqrt.
float gtaoFastACos(float inX)
{
    const float pi = 3.141593;
    const float halfPi = 1.570796;
    float clamped = clamp(inX, -1.0, 1.0);
    float x = abs(clamped);
    float res = -0.156583 * x + halfPi;
    res *= gtaoFastSqrt(1.0 - x);
    return clamped >= 0.0 ? res : pi - res;
}

// XeGTAO_CalculateEdges: 1 = no edge, 0 = full edge, relative to the centre depth.
vec4 gtaoCalculateEdges(float centerZ, float leftZ, float rightZ, float topZ, float bottomZ)
{
    vec4 edgesLRTB = vec4(leftZ, rightZ, topZ, bottomZ) - centerZ;
    float slopeLR = (edgesLRTB.y - edgesLRTB.x) * 0.5;
    float slopeTB = (edgesLRTB.w - edgesLRTB.z) * 0.5;
    vec4 edgesLRTBSlopeAdjusted = edgesLRTB + vec4(slopeLR, -slopeLR, slopeTB, -slopeTB);
    edgesLRTB = min(abs(edgesLRTB), abs(edgesLRTBSlopeAdjusted));
    return clamp(1.25 - edgesLRTB / (centerZ * 0.011), 0.0, 1.0);
}

// Two bits per edge (XeGTAO_PackEdges / XeGTAO_UnpackEdges).
float gtaoPackEdges(vec4 edgesLRTB)
{
    edgesLRTB = floor(clamp(edgesLRTB, 0.0, 1.0) * 2.9 + 0.5);
    return dot(edgesLRTB, vec4(64.0 / 255.0, 16.0 / 255.0, 4.0 / 255.0, 1.0 / 255.0));
}

vec4 gtaoUnpackEdges(float packedValue)
{
    uint packedBits = uint(packedValue * 255.5);
    return clamp(vec4(float((packedBits >> 6) & 3u), float((packedBits >> 4) & 3u),
                      float((packedBits >> 2) & 3u), float(packedBits & 3u)) / 3.0, 0.0, 1.0);
}

// XeGTAO_DepthMIPFilter: the weighted average that keeps the nearer depths (C.1).
float gtaoDepthMipFilter(float depth0, float depth1, float depth2, float depth3)
{
    float maxDepth = max(max(depth0, depth1), max(depth2, depth3));
    const float depthRangeScaleFactor = 0.75; // "found empirically :)"
    float effectRadius = depthRangeScaleFactor * gtao.effectRadius * gtao.radiusMultiplier;
    float falloffRange = gtao.effectFalloffRange * effectRadius;
    float falloffFrom = effectRadius * (1.0 - gtao.effectFalloffRange);
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;
    float weight0 = clamp((maxDepth - depth0) * falloffMul + falloffAdd, 0.0, 1.0);
    float weight1 = clamp((maxDepth - depth1) * falloffMul + falloffAdd, 0.0, 1.0);
    float weight2 = clamp((maxDepth - depth2) * falloffMul + falloffAdd, 0.0, 1.0);
    float weight3 = clamp((maxDepth - depth3) * falloffMul + falloffAdd, 0.0, 1.0);
    float weightSum = weight0 + weight1 + weight2 + weight3;
    return (weight0 * depth0 + weight1 * depth1 + weight2 * depth2 + weight3 * depth3) / weightSum;
}

// Hilbert index + 288 * (NoiseIndex % cycle) driving R2 (vaGTAO.hlsl SpatioTemporalNoise).
// The R2 step is evaluated in 32-bit fixed point, fract(0.5 + index * alpha) exactly,
// instead of in float where index * alpha loses the fraction's low bits.
vec2 gtaoNoise(uint hilbertIndex, uint noiseIndex, uint cycle)
{
    uint index = hilbertIndex + 288u * (noiseIndex % max(cycle, 1u));
    // 0.75487766624669276005 and 0.5698402909980532659114 times 2^32, rounded.
    uvec2 fixedPoint = uvec2(2147483648u) + uvec2(index) * uvec2(3242174889u, 2447445414u);
    return vec2(fixedPoint >> 8) / 16777216.0;
}

// The class channel (gNormal.w, C.5): > 0 thin (leaves, plants, grass, cross-quads,
// translucent particles), < 0 the hand view, 0 solid.
bool gtaoIsHand(float surfaceClass) { return surfaceClass < 0.0; }
bool gtaoIsThin(float surfaceClass) { return surfaceClass > 0.0; }

// Sky: depth at the far plane, as the TAA resolve tests it.
bool gtaoIsSky(float screenDepth) { return screenDepth >= 0.999999; }

#endif
