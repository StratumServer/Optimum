// Native port of the game include normalshading.fsh (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: normalshading.fsh
// optimum-port: verbatim
// optimum-program-uniform: vec3 lightPosition
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_NORMALSHADING_GLSL
#define OPTIMUM_INCLUDE_NORMALSHADING_GLSL

float getBrightnessFromNormal(vec3 normal, float normalShadeIntensity, float minNormalShade) {

	// Option 2: Completely hides peter panning, but makes semi sunfacing block sides pretty dark
	float nb = max(minNormalShade, 0.5 + 0.5 * dot(normal, lightPosition));

	// Let's also define that diffuse light from the sky provides an additional brightness post for up facing stuff
	// because the top side of blocks being darker than the sides is uncanny o__O
	nb = max(nb, dot(normalize(normal), vec3(0, 1, 0)) * 0.95);

	// Let's also lastly define that the north side is always brighter
	float northness = max(0.0, dot(vec3(0,0,-1), normal));
	nb += northness * 0.2;


	return mix(1, nb, normalShadeIntensity);
}

#endif
