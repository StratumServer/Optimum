#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad2d.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad2d.fsh (docs/vulkan.md).
// USEOIT is an axis because oit.fsh gates its outputs on it; the program is registered with Oit = true, and
// in the never-selected USEOIT=0 variant the writes to oit.fsh's outputs are compiled out (family 5 decision).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of particlesquad2d (docs/vulkan.md). Particles have no DRAW
// uniforms, so the push block holds only the sampler slot; everything else is a record member,
// particlesquad2d.vsh's then particlesquad2d.fsh's, each in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, particleTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    int oitPass;
    int withTexture;
    int heldItemMode;
};

#if defined(OPTIMUM_VERTEX)


layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec2 uvIn;					// Per vertex
layout (location = 2) in vec4 baseColor;			// Per vertex

layout (location = 3) in vec4 inColor;				// Per instance
layout (location = 4) in vec3 particlePosition; 	// Per instance
layout (location = 5) in float scale;					// Per instance
layout (location = 6) in float inGlow;				// Per instance

layout(location = 0) out vec4 color;
layout(location = 1) out vec2 uv;
layout(location = 2) out float glowLevel;

void main()
{
	color = baseColor * inColor;
	uv = uvIn;
	glowLevel = inGlow;

	vec3 pos = vec3(vertexPosition.x * scale, vertexPosition.y * scale, vertexPosition.z);

	vec4 cameraPos = modelViewMatrix * vec4(pos + particlePosition, 1.0);
	gl_Position = projectionMatrix * cameraPos;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec4 color;
layout(location = 1) in vec2 uv;
layout(location = 2) in float glowLevel;



#include "oit.glsl"

void main()
{
	vec4 outColor;

	if (heldItemMode > 0) {
		// Ensure held item always being in the front
		gl_FragDepth = gl_FragCoord.z / 20;
	} else {
		gl_FragDepth = gl_FragCoord.z;
	}

	if (withTexture > 0) {
		outColor = color * texture(optimumTextures2D[particleTex], uv);
	} else {
		outColor = color;
	}

	if (outColor.a < 0.002) discard;


#if USEOIT == 1
	if (oitPass > 0) {
		// Dunno why but with this modifier the torch particles look more similar when held versus placed
		outColor.a*=1;

        OIT(outColor, glowLevel);

	} else {
		OITreveal = outColor;
	}
#endif
}

#endif
