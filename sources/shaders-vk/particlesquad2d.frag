#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlesquad2d.fsh (docs/vulkan-native-shaders.md).
// USEOIT is an axis because oit.fsh gates its outputs on it; the program is registered with Oit = true, and
// in the never-selected USEOIT=0 variant the writes to oit.fsh's outputs are compiled out (family 5 decision).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "particlesquad2d.interface.glsl"

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
