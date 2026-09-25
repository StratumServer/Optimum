#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktransparent.vsh (vanilla, docs/vulkan.md). Axis: USESSBO (attribute
// layout and FaceData).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktransparent.fsh (vanilla, docs/vulkan.md). Axis: USEOIT, through
// include/oit.glsl, which declares the six OIT outputs and OIT() only when it is 1. The client registers
// chunktransparent with Oit = true (ShaderProgramBase's default), so USEOIT=0 is never linked; the GLSL 330
// program has no outputs and no OIT() there either, so the 0 variant skips the call and writes nothing.
// SHINYEFFECT is a specialization-constant branch.
//
// fogandlight.frag.glsl and fogspheres.glsl read names owned by fogandlight.vsh and vertexwarp.vsh, which the
// vertex stage includes, so this stage activates those owners' names itself (contract section 3).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunktransparent (docs/vulkan.md). One draw per mesh pool:
// the push block holds the sampler slot, then origin, modelViewMatrix and forcedTransparency (84 B). The
// record holds every other uniform, chunktransparent.vsh's first (the previous-frame warp state
// vertexwarp.glsl reads comes with its include), then underwatereffects' frameSize.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    vec3 origin;
    mat4 modelViewMatrix;
    float forcedTransparency;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogDensityIn;
    float fogMinIn;
    mat4 projectionMatrix;
    float subpixelPaddingX;
    float subpixelPaddingY;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;

    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in int colormapData;
 #endif



layout(location = 0) out vec4 rgba;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out float fogAmount;
layout(location = 3) out vec2 uv;
layout(location = 4) out vec4 worldPos;
layout(location = 5) out vec3 vertexPos;

layout(location = 6) flat out int renderFlags;
layout(location = 7) flat out vec3 normal;
layout(location = 8) out float normalShadeIntensity;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"
#include "colormap.vert.glsl"

 #if USESSBO > 0
layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexIndex / 4];
	int vIndex = gl_VertexIndex & 0x03;
	renderFlags = vdata.flags[vIndex];
	vec3 xyz = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;

 #endif

	vertexPos = xyz;

	vec4 truePos = vec4(xyz + origin, 1.0);
	worldPos = truePos;

	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

	calcShadowMapCoords(modelViewMatrix, worldPos);

 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = uvIn;
 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgba.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5 + max(0.0, worldPos.y * 0.02), -1.0, 1.0) - forcedTransparency;

	rgbaFog = rgbaFogIn;

	// To fix Z-Fighting on blocks over certain other blocks.
	if (gl_Position.z > 0) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / max(3, gl_Position.z * 0.05);
	}

	normal = unpackNormal(renderFlags);
	normalShadeIntensity = min(1, rgbaLightIn.a * 1.5);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) in vec4 rgba;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in float fogAmount;
layout(location = 3) in vec2 uv;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in vec4 worldPos;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
layout(location = 5) in vec3 vertexPos;

layout(location = 8) in float normalShadeIntensity;
layout(location = 6) flat in int renderFlags;
layout(location = 7) flat in vec3 normal;

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "colormap.frag.glsl"
#include "underwatereffects.glsl"
#include "oit.glsl"

void main()
{
	// When looking through tinted glass you can clearly see the edges where we fade to sky color
	// Using this discard seems to completely fix that
	if (rgba.a < 0.005) discard;

	vec4 texColor = rgba * getColorMapped(optimumTextures2D[terrainTex], texture(optimumTextures2D[terrainTex], uv));

	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPos.xyz, 0);

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		texColor = applyFogAndShadowWithNormal(texColor, 0, normal, normalShadeIntensity, 0.45, worldPos.xyz);
		texColor.rgb = applyUnderwaterEffects(texColor.rgb, murkiness);
	} else {
		texColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, normalShadeIntensity, 0.45, worldPos.xyz);
	}


	if (OPTIMUM_SHINYEFFECT > 0) {
	float glow=0;
	texColor = mix(applyReflectiveEffect(texColor, glow, renderFlags, uv, normal, worldPos, worldPos, blockLight), texColor, min(1, 2 * fogAmount));
	}

#if USEOIT > 0
    OIT(texColor, glowLevel);
#endif

}

#endif
