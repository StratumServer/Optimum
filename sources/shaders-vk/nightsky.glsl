#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of nightsky.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of nightsky.fsh (docs/vulkan.md). Axis: GBUFFER (the G-buffer outputs).
// outColor has no location in GLSL 330; it is location 0, the one GL and ProgramInterfaceLayout assign.
// worldPosY is read by nothing and written by no vertex stage, as in GLSL 330.
// The vertex stage includes fogandlight.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of nightsky (docs/vulkan.md). One draw per Use(): the push
// block holds only the cube map's slot; every other uniform is a record member: nightsky.vsh's,
// nightsky.fsh's (ditherSeed, horizontalResolution and playerToSealevelOffset are its own here, because
// skycolor.fsh, their frame owner, is not included), then fogandlight.fsh's windWaveCounter and
// underwatereffects.fsh's frameSize.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(samplerCube, ctex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelMatrix;
    mat4 viewMatrix;

    vec4 rgbaFog;
    int ditherSeed;
    int horizontalResolution;
    float dayLight;
    float horizonFog;
    float playerToSealevelOffset;
    float fogDensityIn;
    float fogMinIn;

    float windWaveCounter;
    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPosition;

layout(location = 0) out vec3 texCoords;
layout(location = 2) out float nightVisionStrengthv;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main () {
  texCoords = vertexPosition;
  vec4 worldPos = modelMatrix * vec4(vertexPosition, 1.0);
  nightVisionStrengthv = nightVisionStrength * 0.33;

  gl_Position = projectionMatrix * viewMatrix * worldPos;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec3 texCoords;
layout(location = 1) in float worldPosY;
layout(location = 2) in float nightVisionStrengthv;


layout(location = 0) out vec4 outColor;
#if GBUFFER == 1
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


#include "dither.glsl"
#include "fogandlight.frag.glsl"
#include "underwatereffects.glsl"

void main () {
	vec4 skyCol = texture (optimumTexturesCube[ctex], texCoords) + NoiseFromPixelPosition(ivec2(gl_FragCoord.xy), ditherSeed, horizontalResolution);
	skyCol -= 0.03f;
	skyCol.rgb *= 2;
	skyCol.a = max(0.0, 1 - 2*(dayLight - 0.05));

	outColor = skyCol;
	outColor.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;

	float murkiness=getSkyMurkiness();
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if GBUFFER == 1
	outGPosition = vec4(0);
	outGNormal = vec4(0);
#endif

}

#endif
