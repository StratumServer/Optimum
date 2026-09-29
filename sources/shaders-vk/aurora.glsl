#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of aurora.vsh (docs/vulkan.md).
// aurora.vsh declares shadowCoordsFar/Near itself under SHADOWQUALITY (it does not include shadowcoords.vsh)
// and never writes them. SHADOWQUALITY is a specialization constant, so they are declared unconditionally at
// the locations fogandlight.fsh reads, and stay unwritten as in GLSL 330.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of aurora.fsh (docs/vulkan.md).
// USEOIT is an axis because oit.fsh gates its outputs on it; the program is registered with Oit = true, and
// in the never-selected USEOIT=0 variant the writes to oit.fsh's outputs are compiled out (family 5 decision).
// The vertex stage includes fogandlight.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of aurora (docs/vulkan.md). One draw per Use(): the push block
// holds only the sampler slot; every other uniform is a record member: aurora.vsh's, aurora.fsh's (its second
// auroraCounter is the same name), then fogandlight.fsh's windWaveCounter (its owner vertexwarp.vsh is not
// included).
//
// extraGodray = 0 and alphaTest = 0.001 are GLSL 330 initializers; the runtime seeds them (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 color;
    vec4 rgbaTint;
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaBlockIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    float auroraCounter;

    float extraGodray;
    float alphaTest;

    float windWaveCounter;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in float xposIn;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 col;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec4 vexPos;
layout(location = 5) out float xpos;

layout(location = 6) flat out int renderFlags;

layout(location = OPTIMUM_LOCATION_SHADOW_COORDS_FAR) out vec4 shadowCoordsFar;
layout(location = OPTIMUM_LOCATION_SHADOW_COORDS_NEAR) out vec4 shadowCoordsNear;

#include "vertexflagbits.glsl"
#include "fogandlight.vert.glsl"
#include "noise3d.glsl"

void main(void)
{
	vexPos = vec4(vertexPositionIn, 1.0);

	vexPos.x += 100*cnoise(vec3((vexPos.x)/100.0, (vexPos.z)/500.0, auroraCounter/3));
	vexPos.z += 100*cnoise(vec3((vexPos.x)/120.0, (vexPos.z)/300.0, auroraCounter/3));

	vec4 camPos = modelViewMatrix * vexPos;

	uv = uvIn;
	xpos = xposIn;
	col = color;
	rgbaFog = rgbaFogIn;

	gl_Position = projectionMatrix * camPos;

	fogAmount = getFogLevel(vec4(vertexPositionIn, 1), fogMinIn, fogDensityIn);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 col;
layout(location = 2) in vec4 rgbaFog;
layout(location = 4) in vec4 vexPos;
layout(location = 5) in float xpos;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 6) flat in int renderFlags;



#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "oit.glsl"

void main () {
	vec4 outColor = vec4(1);

	outColor = applyFogAndShadow(outColor, fogAmount);

	if (outColor.a < alphaTest) discard;

	float rndr = min(0.6, cnoise(vec3(vexPos.x/500.0, -vexPos.z/600.0, auroraCounter))*2.3 - 1);
	float rndg = cnoise(vec3(vexPos.x/600.0, vexPos.z/600.0, 1 - auroraCounter))/3;
	float rndb = cnoise(vec3(vexPos.x/600.0, vexPos.z/600.0, 1 - auroraCounter))/5;

	outColor = vec4(
		clamp(rndr, 0, 1),
		clamp(0.5 + rndg - rndr, 0, 0.8),
		clamp(0.5 + rndb, 0, 1),
		max(0, 0.7 - uv.y * 0.7)/2
	);

	float advx = xpos * 10; // was uv.x

	outColor.a *=
		1
		+ 0.7 * cnoise(vec3(advx/6 + (vexPos.x)/120.0, uv.y/2 + (vexPos.z)/120.0, auroraCounter))
		+ 0.5 * cnoise(vec3(advx/3 + (vexPos.x)/60.0, uv.y/1.5 + (vexPos.z)/60.0, auroraCounter))
		+ 0.2 * cnoise(vec3(advx/1.5 + (vexPos.x)/30.0, uv.y + (vexPos.z)/30.0, auroraCounter))
	;


	//outColor.a *= 0.7;

	outColor.a *= clamp(9*uv.y - 1, 0, 1);

	//outColor.a=1;


	outColor.a = clamp(outColor.a, 0, 1);

	outColor *= col;

	// Fade edges
	// http://fooplot.com/#W3sidHlwZSI6MCwiZXEiOiJtaW4oMSxtaW4oMTAqeCwoMS14KSoxMCkpIiwiY29sb3IiOiIjMDAwMDAwIn0seyJ0eXBlIjoxMDAwLCJ3aW5kb3ciOlsiMCIsIjEiLCIwIiwiMiJdLCJzaXplIjpbNjQ5LDM5OV19XQ--
	float a = min((xpos - 0.1) * 20, (1 - xpos) * 20);
	outColor.a *= min(1, a);

#if USEOIT == 1
	OIT(clamp(outColor, vec4(0.0), vec4(1.0)), 0.0);
	outGlow = vec4(1, extraGodray, 0, min(1, outColor.a * 6));
#endif

}

#endif
