// Native port of the game include shadowcoords.vsh (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: shadowcoords.vsh
// optimum-port: transformed
// optimum-frame-owner: shadowcoords.vsh
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).
//
// SHADOWQUALITY is the specialization constant OPTIMUM_SHADOWQUALITY: the outputs are declared
// unconditionally and the #if blocks are branches on the constant, with the same expressions.

#ifndef OPTIMUM_INCLUDE_SHADOWCOORDS_GLSL
#define OPTIMUM_INCLUDE_SHADOWCOORDS_GLSL

#define OPTIMUM_FRAME_OWNER_SHADOWCOORDS_VSH
#include "frame.glsl"
#include "varyings.glsl"
#include "specialization.glsl"

layout(location = OPTIMUM_LOCATION_SHADOW_COORDS_FAR) out vec4 shadowCoordsFar;
layout(location = OPTIMUM_LOCATION_SHADOW_COORDS_NEAR) out vec4 shadowCoordsNear;


const float transitionDistance = 10.0;


void calcShadowMapCoords(mat4 modelviewMat, vec4 worldPos) {
	float nearSub = 0;
	float len = 0;
	if (OPTIMUM_SHADOWQUALITY > 0) {
		len = length(worldPos);
	}

	if (OPTIMUM_SHADOWQUALITY > 1) {
		// Near map
		shadowCoordsNear = toShadowMapSpaceMatrixNear * worldPos;

		float distanceNear = clamp(
			max(max(0.0, 0.03 - shadowCoordsNear.x) * 100, max(0.0, shadowCoordsNear.x - 0.97) * 100) +
			max(max(0.0, 0.03 - shadowCoordsNear.y) * 100, max(0.0, shadowCoordsNear.y - 0.97) * 100) +
			max(0.0, shadowCoordsNear.z - 0.98) * 100 +
			max(0.0, len / shadowRangeNear - 0.15)
		, 0.0, 1.0);

		nearSub = shadowCoordsNear.w = clamp(1.0 - distanceNear, 0.0, 1.0);
		if (shadowCoordsNear.z >= 0.999) shadowCoordsNear.w = 0.0;     // so no need to test both in fogandlight.fsh
	}

	if (OPTIMUM_SHADOWQUALITY > 0) {
		// Far map
		shadowCoordsFar = toShadowMapSpaceMatrixFar * worldPos;

		float distanceFar = clamp(
			max(max(0.0, 0.03 - shadowCoordsFar.x) * 10, max(0.0, shadowCoordsFar.x - 0.97) * 10) +
			max(max(0.0, 0.03 - shadowCoordsFar.y) * 10, max(0.0, shadowCoordsFar.y - 0.97) * 10) +
			max(0.0, shadowCoordsFar.z - 0.98) * 10 +
			max(0.0, len / shadowRangeFar - 0.15)
		, 0.0, 1.0);

		distanceFar = distanceFar * 2 - 0.5;

		shadowCoordsFar.w = max(0.0, clamp(1.0 - distanceFar, 0.0, 1.0) - nearSub);
		if (shadowCoordsFar.z >= 0.999) shadowCoordsFar.w = 0.0;     // so no need to test both in fogandlight.fsh
	}
}

#endif
