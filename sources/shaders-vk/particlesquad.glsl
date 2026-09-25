#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad.fsh (docs/vulkan.md).
// USEOIT is an axis because oit.fsh gates its outputs on it. The program is registered with Oit = true, so
// only USEOIT=1 is ever selected; the USEOIT=0 variant exists because the builder compiles every axis value,
// and there the OIT call (which has nothing to write to) is compiled out (docs/vulkan.md
// section 9.1).
// The vertex stage includes fogandlight.vsh and vertexwarp.vsh (contract section 3, cross-stage owners).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of particlesquad (docs/vulkan.md). Particles have no DRAW
// uniforms, so the push block holds only the sampler slot; everything else is a record member:
// particlesquad.vsh's, vertexwarp.vsh's previous-frame mirrors, then underwatereffects.fsh's frameSize.
// The prev* initializers are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, particleTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

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


layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec2 uvIn;					// Per vertex
layout (location = 2) in vec4 baseColor;			// Per vertex

layout (location = 3) in int renderFlags;	 		// Per instance
layout (location = 4) in vec3 particlePosition; 	// Per instance
layout (location = 5) in float scale;					// Per instance
layout (location = 6) in vec4 particleDir; 			// Per instance
layout (location = 7) in vec4 rgbaLightIn; 		// Per instance
layout (location = 8) in vec4 rgbaBlockIn; 		// Per instance

layout(location = 0) out vec4 color;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out vec3 vexPos;
layout(location = 3) out float fogAmount;
layout(location = 4) out float extraWeight;
layout(location = 5) out vec2 uv;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

mat4 rotationZ( in float angle ) {
	return mat4(	cos(angle),		-sin(angle),	0,	0,
			 		sin(angle),		cos(angle),		0,	0,
							0,				0,		1,	0,
							0,				0,		0,	1);
}


void main()
{
	mat4 mvmat = modelViewMatrix;
	// This makes all snow particles submerged :<
	//vexPos = vertexPosition * scale - 0.125 * scale;

	vexPos = vertexPosition * scale;

	bool rainParticle = renderFlags < 0; //(renderFlags & (1<<31)) > 0;

	extraWeight = (renderFlags & (1<<9)) > 0 ? 1 : 10;

	uv = uvIn;
	if (rainParticle) uv = vec2(0.5, 0.5);

	// 1. Translate the particle
	mvmat[3] = mvmat * vec4(particlePosition, 1.0);

	if (rainParticle) {
		vec3 u = particleDir.xyz; // your input vector
		vec3 v = vec3(0.0, -1.0, 0.0); // your other input vector

		u = normalize(u);

		float zangle = acos( dot( u.xy, v.xy ) );
		mvmat = mvmat * rotationZ(zangle);
	}


	// 2. Billboard the particle
	mvmat[0].xyz = vec3(1.0, 0.0, 0.0);

	if (!rainParticle) {
		mvmat[1].xyz = vec3(0.0, 1.0, 0.0);
	}

	mvmat[2].xyz = vec3(0.0, 0.0, 1.0);



	// 3. Lighting
	vec4 worldPos = vec4(vexPos, 1.0);
	if (rainParticle) {
		worldPos.y = worldPos.y * 8 - 3;
		worldPos.xz /= 3.5;
	}


	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = mvmat * worldPos;
	color = baseColor * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos) * rgbaBlockIn;
	color.a = rgbaBlockIn.a;
	rgbaFog = rgbaFogIn;

	if (rainParticle) {
		color.a = min(1, 1.05*color.a * (1.2 - clamp(1 - 7*vexPos.y, 0, 1)));
	}

	calcShadowMapCoords(mvmat, vec4(worldPos.x + particlePosition.x, worldPos.y + particlePosition.y, worldPos.z + particlePosition.z, worldPos.w));

	// 4. Done.
	gl_Position = projectionMatrix * cameraPos;
	fogAmount = getFogLevel(vec4(particlePosition, 0), fogMinIn, fogDensityIn);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec4 color;
layout(location = 5) in vec2 uv;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in vec3 vexPos;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in float extraWeight;



#include "fogandlight.frag.glsl"
#include "underwatereffects.glsl"
#include "oit.glsl"

void main()
{
	vec4 outColor;

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadow(color, 0);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadow(color, fogAmount);
	}

	vec2 uvdist = vec2(
		max(max(0.0, 0.1 - uv.x), max(0.0, uv.x - 0.9)),
		max(max(0.0, 0.1 - uv.y), max(0.0, uv.y - 0.9))
	);

	outColor.a *= 1 - length(uvdist)*10;

#if USEOIT == 1
    OIT(clamp(outColor, vec4(0.0), vec4(1.0)), glowLevel);
#endif

}

#endif
