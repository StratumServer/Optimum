#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of sky.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of sky.fsh (docs/vulkan.md). Axis: GBUFFER (the G-buffer outputs).
// The vertex stage includes fogandlight.vsh, which owns the flatFogDensity and fogSpheres that
// fogandlight.fsh and skycolor.fsh read here (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of sky (docs/vulkan.md). One draw per Use() and no sampler of
// its own (sky and glow are set 0 frame textures), so there is no push block; every uniform is a record
// member: sky.vsh's, sky.fsh's, then fogandlight.fsh's windWaveCounter (vertexwarp.vsh, its owner, is not
// included) and underwatereffects.fsh's frameSize.
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float fogDensityIn;
    float fogMinIn;
    float dayLight;
    float horizonFog;
    vec3 playerPos;
    vec3 sunPosition;

    float windWaveCounter;
    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;

layout(location = 0) out vec3 vertexPosition;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out float nightVisionStrengthv;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main()
{
	vertexPosition = vertexPositionIn;
	rgbaFog = rgbaFogIn;
	nightVisionStrengthv = nightVisionStrength;
	vec4 cameraPos = modelViewMatrix * vec4(vertexPosition, 1.0);

    gl_Position = projectionMatrix * cameraPos;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in float nightVisionStrengthv;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include "dither.glsl"
#include "fogandlight.frag.glsl"
#include "skycolor.glsl"
#include "underwatereffects.glsl"

void main()
{
	outColor = vec4(1);
	outGlow = vec4(1);
	float sealevelOffsetFactor = 0.25;
	getSkyColorAt(vertexPosition, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, outColor, outGlow);

	if (psychedelicStrength > Epsilon) outColor = applyPsychedelicEffect(outColor, vertexPosition.xyz/2, 0);

	float murkiness = max(0.0, getSkyMurkiness() - 14*fogDensityIn);
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

	outColor.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;
	outGlow.y *= clamp((dayLight - 0.05) * 2 - 50*murkiness, 0, 1);

#if GBUFFER == 1
	outGPosition = vec4(0);
	outGNormal = vec4(0);
#endif

}

#endif
