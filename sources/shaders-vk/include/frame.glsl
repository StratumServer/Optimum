// Generated from Optimum.Render.Vulkan/Shaders/FrameGlobals.cs (FrameGlobals.GenerateInclude).
// Do not edit: FrameGlobalsTests regenerates this file and fails on any difference.
//
// The FrameGlobals block (docs/vulkan-native-shaders.md section 3): set 0, binding 0, scalar
// layout, bound with a dynamic offset. Members sit at the offsets the renderer writes.
//
// No member is a global name here. A member is the shared frame value only in a program that
// includes the member's owner file, so every owner has its own group of defines below. An
// owner include (fogandlight.frag.glsl, vertexwarp.glsl, ...) defines its owner macro and
// includes this file, which activates its group. A program whose other stage includes an
// owner defines that owner's macro itself before its includes (for example
// OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH in a fragment stage that reads flatFogDensity), and a
// program that includes no owner of a name declares the name in its own record instead.

#ifndef OPTIMUM_FRAME_GLSL
#define OPTIMUM_FRAME_GLSL

#extension GL_EXT_scalar_block_layout : require

#include "bindings.glsl"

layout(set = OPTIMUM_SET_FRAME, binding = OPTIMUM_BINDING_FRAME_GLOBALS, scalar) uniform OptimumFrameGlobals
{
    layout(offset = 0) float zNear;
    layout(offset = 4) float zFar;
    layout(offset = 8) vec3 lightPosition;
    layout(offset = 20) float shadowIntensity;
    layout(offset = 24) float glitchStrength;
    layout(offset = 28) float psychedelicStrength;
    layout(offset = 32) float shadowMapWidthInv;
    layout(offset = 36) float shadowMapHeightInv;
    layout(offset = 40) float viewDistance;
    layout(offset = 44) float viewDistanceLod0;
    layout(offset = 48) int fogSphereQuantity;
    layout(offset = 52) int pointLightQuantity;
    layout(offset = 56) float flatFogDensity;
    layout(offset = 60) float flatFogStart;
    layout(offset = 64) float glitchStrengthFL;
    layout(offset = 68) float nightVisionStrength;
    layout(offset = 72) float shadowRangeNear;
    layout(offset = 76) float shadowRangeFar;
    layout(offset = 80) float timeCounter;
    layout(offset = 84) float windWaveCounter;
    layout(offset = 88) float windWaveCounterHighFreq;
    layout(offset = 92) float windSpeed;
    layout(offset = 96) float waterWaveCounter;
    layout(offset = 100) vec3 playerpos;
    layout(offset = 112) float globalWarpIntensity;
    layout(offset = 116) float glitchWaviness;
    layout(offset = 120) float windWaveIntensity;
    layout(offset = 124) float waterWaveIntensity;
    layout(offset = 128) int perceptionEffectId;
    layout(offset = 132) float perceptionEffectIntensity;
    layout(offset = 136) float fogWaveCounter;
    layout(offset = 140) float sunsetMod;
    layout(offset = 144) int ditherSeed;
    layout(offset = 148) int horizontalResolution;
    layout(offset = 152) float playerToSealevelOffset;
    layout(offset = 156) float seasonRel;
    layout(offset = 160) float seaLevel;
    layout(offset = 164) float atlasHeight;
    layout(offset = 168) float seasonTemperature;
    layout(offset = 172) float cameraUnderwater;
    layout(offset = 176) vec4 waterMurkColor;
    layout(offset = 192) mat4 toShadowMapSpaceMatrixNear;
    layout(offset = 256) mat4 toShadowMapSpaceMatrixFar;
    layout(offset = 320) float fogSpheres[24];
    layout(offset = 416) vec4 colorMapRects[40];
    layout(offset = 1056) vec3 pointLights[100];
    layout(offset = 2256) vec3 pointLightColors[100];
} optimumFrame;

// Block size: 3456 bytes.

#endif

// fogandlight.fsh
#if defined(OPTIMUM_FRAME_OWNER_FOGANDLIGHT_FSH) && !defined(OPTIMUM_FRAME_NAMES_FOGANDLIGHT_FSH)
#define OPTIMUM_FRAME_NAMES_FOGANDLIGHT_FSH
#define zNear optimumFrame.zNear
#define zFar optimumFrame.zFar
#define lightPosition optimumFrame.lightPosition
#define shadowIntensity optimumFrame.shadowIntensity
#define glitchStrength optimumFrame.glitchStrength
#define psychedelicStrength optimumFrame.psychedelicStrength
#define shadowMapWidthInv optimumFrame.shadowMapWidthInv
#define shadowMapHeightInv optimumFrame.shadowMapHeightInv
#endif

// fogandlight.vsh
#if defined(OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH) && !defined(OPTIMUM_FRAME_NAMES_FOGANDLIGHT_VSH)
#define OPTIMUM_FRAME_NAMES_FOGANDLIGHT_VSH
#define viewDistance optimumFrame.viewDistance
#define viewDistanceLod0 optimumFrame.viewDistanceLod0
#define fogSphereQuantity optimumFrame.fogSphereQuantity
#define pointLightQuantity optimumFrame.pointLightQuantity
#define flatFogDensity optimumFrame.flatFogDensity
#define flatFogStart optimumFrame.flatFogStart
#define glitchStrengthFL optimumFrame.glitchStrengthFL
#define nightVisionStrength optimumFrame.nightVisionStrength
#define fogSpheres optimumFrame.fogSpheres
#define pointLights optimumFrame.pointLights
#define pointLightColors optimumFrame.pointLightColors
#endif

// shadowcoords.vsh
#if defined(OPTIMUM_FRAME_OWNER_SHADOWCOORDS_VSH) && !defined(OPTIMUM_FRAME_NAMES_SHADOWCOORDS_VSH)
#define OPTIMUM_FRAME_NAMES_SHADOWCOORDS_VSH
#define shadowRangeNear optimumFrame.shadowRangeNear
#define shadowRangeFar optimumFrame.shadowRangeFar
#define toShadowMapSpaceMatrixNear optimumFrame.toShadowMapSpaceMatrixNear
#define toShadowMapSpaceMatrixFar optimumFrame.toShadowMapSpaceMatrixFar
#endif

// vertexwarp.vsh
#if defined(OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH) && !defined(OPTIMUM_FRAME_NAMES_VERTEXWARP_VSH)
#define OPTIMUM_FRAME_NAMES_VERTEXWARP_VSH
#define timeCounter optimumFrame.timeCounter
#define windWaveCounter optimumFrame.windWaveCounter
#define windWaveCounterHighFreq optimumFrame.windWaveCounterHighFreq
#define windSpeed optimumFrame.windSpeed
#define waterWaveCounter optimumFrame.waterWaveCounter
#define playerpos optimumFrame.playerpos
#define globalWarpIntensity optimumFrame.globalWarpIntensity
#define glitchWaviness optimumFrame.glitchWaviness
#define windWaveIntensity optimumFrame.windWaveIntensity
#define waterWaveIntensity optimumFrame.waterWaveIntensity
#define perceptionEffectId optimumFrame.perceptionEffectId
#define perceptionEffectIntensity optimumFrame.perceptionEffectIntensity
#endif

// skycolor.fsh
#if defined(OPTIMUM_FRAME_OWNER_SKYCOLOR_FSH) && !defined(OPTIMUM_FRAME_NAMES_SKYCOLOR_FSH)
#define OPTIMUM_FRAME_NAMES_SKYCOLOR_FSH
#define fogWaveCounter optimumFrame.fogWaveCounter
#define sunsetMod optimumFrame.sunsetMod
#define ditherSeed optimumFrame.ditherSeed
#define horizontalResolution optimumFrame.horizontalResolution
#define playerToSealevelOffset optimumFrame.playerToSealevelOffset
#endif

// colormap.vsh
#if defined(OPTIMUM_FRAME_OWNER_COLORMAP_VSH) && !defined(OPTIMUM_FRAME_NAMES_COLORMAP_VSH)
#define OPTIMUM_FRAME_NAMES_COLORMAP_VSH
#define seasonRel optimumFrame.seasonRel
#define seaLevel optimumFrame.seaLevel
#define atlasHeight optimumFrame.atlasHeight
#define seasonTemperature optimumFrame.seasonTemperature
#define colorMapRects optimumFrame.colorMapRects
#endif

// underwatereffects.fsh
#if defined(OPTIMUM_FRAME_OWNER_UNDERWATEREFFECTS_FSH) && !defined(OPTIMUM_FRAME_NAMES_UNDERWATEREFFECTS_FSH)
#define OPTIMUM_FRAME_NAMES_UNDERWATEREFFECTS_FSH
#define cameraUnderwater optimumFrame.cameraUnderwater
#define waterMurkColor optimumFrame.waterMurkColor
#endif
