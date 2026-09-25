#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of cloudmap.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of cloudmap.fsh (docs/vulkan.md).
//
// The dither stub: cloudmap.fsh defines NoiseFromPixelPosition(a, b, c) as vec4(0.0) before including
// skycolor.fsh, so the sky glow it samples carries no dither. skycolor.glsl includes dither.glsl, whose
// function definition that macro would rewrite, so dither.glsl is included first and the stub is defined
// after it: the function exists (unused), and every call skycolor.glsl makes still expands to vec4(0.0),
// exactly as in GLSL 330 (family 5 decision).
//
// The DYNLIGHTS preprocessor branches are OPTIMUM_DYNLIGHTS branches with the same code; the arrays they
// gated are declared unconditionally (contract section 5).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of cloudmap (docs/vulkan.md). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in cloudmap.fsh's declaration order, and every
// other uniform is a record member: cloudmap.fsh's own, then the program uniforms of fogandlight.fsh and
// fogspheres.ash, whose frame owners (fogandlight.vsh, vertexwarp.vsh) this program does not include.
//
// pointLightQuantity, pointLights, pointLightColors and nightVisionStrength are cloudmap.fsh's own uniforms
// (it declares them itself), not frame members. The arrays are sized by FrameGlobals.MaxDynamicLights (100),
// as the frame block's copies are (section 5); pointLightQuantity bounds the loop.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, mapData1);
    OPTIMUM_SAMPLER_SLOT(sampler2D, mapData2);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float dayLight;
    float globalCloudBrightness;
    float time;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    vec3 sunPosition;
    float nightVisionStrength;
    float alpha;

    float width;
    vec3 mapOffset;
    vec2 mapOffsetCentre;
    mat4 viewMatrix;

    int pointLightQuantity;
    vec3 pointLights[100];
    vec3 pointLightColors[100];

    float flatFogDensity;
    float flatFogStart;
    float viewDistance;
    float viewDistanceLod0;
    float windWaveCounter;
    float fogSpheres[24];
    int fogSphereQuantity;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec2 p;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 ndc;

void main(){
    gl_Position = vec4(p, 0.0, 1.0);
    uv = p * 0.5 + 0.5;
    ndc = p;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;
layout(location = 1) in vec2 ndc;

layout(location = 0) out vec4 tile;
layout(location = 1) out vec4 colour;

#define rgbaFog rgbaFogIn
#include "fogandlight.frag.glsl"
#include "dither.glsl"
#define NoiseFromPixelPosition(a, b, c) vec4(0.0)
#include "skycolor.glsl"

vec4 getPointLightRgbvl(vec3 worldPos) {
    if (OPTIMUM_DYNLIGHTS == 0) {
    return vec4(0);
    }

    vec4 pointColSum = vec4(0);
    float bPointBrightSum = 0;

    for (int i = 0; i < pointLightQuantity; i++) {
        vec4 lightVec = -vec4(worldPos.x - pointLights[i].x, worldPos.y - pointLights[i].y, worldPos.z - pointLights[i].z, 1);
        vec3 color = pointLightColors[i];
        if (color.r > 10) {
            color /= 200; // This is a Lightning strike point light
        }

        float dist = pow(1.35, length(lightVec) / 4.0);
        float bright = (color.r + color.g + color.b);
        float strength = min(bright/3, bright / dist);

        pointColSum.w = max(pointColSum.w, strength);
        bPointBrightSum += strength;

        pointColSum.r += color.r * strength;
        pointColSum.g += color.g * strength;
        pointColSum.b += color.b * strength;
    }

    if (bPointBrightSum > 0) {
        pointColSum.rgb /= max(1, bPointBrightSum);
    }

//  pointColSum.w /= max(1, glitchStrengthFL * 2);

    return pointColSum;
}

float getFogLevel(vec4 worldPos, float fogMin, float fogDensity) {
    float depth = length(worldPos.xyz);
    float clampedDepth = min(250, depth);
    float heightDiff = worldPos.y - flatFogStart;
    float extraDistanceFog = max(-flatFogDensity * clampedDepth * (flatFogStart) / 60, 0); // div 60 was 160 before, at 160 thick flat fog looks broken when looking at trees
    float distanceFog = 1 - 1 / exp(clampedDepth * fogDensity + extraDistanceFog);

    float flatFog = 1 - 1 / exp(heightDiff * flatFogDensity);

    float val = max(flatFog, distanceFog);
    float nearnessToPlayer = clamp((8-depth)/8, 0, 0.9);
    val = max(min(0.04, val), val - nearnessToPlayer);

    // Needs to be added after so that underwater fog still gets applied.
    val += fogMin;

    return clamp(val, 0, 1);
}

void main(){

    const float cloudTileSize = 50.0;

    vec4 data1 = texelFetch(optimumTextures2D[mapData1], ivec2(uv * width), 0);
    vec4 data2 = texelFetch(optimumTextures2D[mapData2], ivec2(uv * width), 0);
    float thinCloudMode      = data1.r;
    float selfThickness      = data1.g;
    float cloudOpaqueness    = data1.b;
    float cloudBrightness    = data1.a;
    float undulatingModeness = data2.r;

    vec2 v = uv * width - width / 2.0;
    vec3 tilePosition = vec3(mapOffset.xz + v * cloudTileSize, mapOffset.y).xzy;
    vec3 viewSpace = vec3(viewMatrix * vec4(tilePosition, 1.0));

    float undulate = gnoise(vec3((v + mapOffsetCentre) * vec2(0.5, 0.2), time * 0.15)) * undulatingModeness;

    float linearfade = abs(length((uv + mapOffset.xz/cloudTileSize/width) * 2.0 - 1.0));

    float opaque = min(1.0, cloudOpaqueness * min(1.0, 10.0 * selfThickness)) * 2.0;
    opaque += undulatingModeness * 4.0;
    opaque *= alpha;
    opaque *= smoothstep(0.95, 0.9, linearfade);

    float greyscale = smoothstep(0.0, 1.1, dayLight)
                    * (0.1 + cloudBrightness * 0.7)
                    * globalCloudBrightness
	;
    greyscale *= mix(1, 0.4 + 0.5*(undulate + 0.5), undulatingModeness);

    float height = (500.0 - 500.0 * thinCloudMode)
                 * max(0.0, 1.0 - 3.0 * thinCloudMode)
                 * pow(selfThickness - 0.1, 2.0);

    float lo = (-12.5 - height * 0.05 - undulate * 25.0) / cloudTileSize;
    float hi = ( 12.5 + height        - undulate * 25.0) / cloudTileSize;

    colour = vec4(vec3(greyscale), 1.0);

    float sealevelOffsetFactor = 0.25;
    float dayLight = 1;
    float horizonFog = 0;
    // Due to earth curvature the clouds are actually lower, so we do +100 to not have them dismissed during sunglow coloring
    vec4 skyGlow = getSkyGlowAt(vec3(tilePosition.x, 100.0, tilePosition.z), sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, 0.7);
    colour.rgb *= mix(vec3(1.0), 1.2 * skyGlow.rgb, skyGlow.a);
    colour.rgb *= max(1, 0.9 + skyGlow.a/10);


    float fogAmount = getFogLevel(vec4(tilePosition.x, 0.0, tilePosition.z, 1.0), fogMinIn, fogDensityIn);
    float fogAmountf = clamp(fogAmount + clamp(1 - 4 * dayLight, -0.04, 1), 0, 1);

    colour.rgb = mix(colour.rgb, rgbaFogIn.rgb, fogAmountf);
    colour.rgb += getPointLightRgbvl(viewSpace).rgb * 0.7;
    colour.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrength;

    tile.r = opaque;
    tile.g = 0.0;
    tile.b = lo;
    tile.a = hi;

}

#endif
