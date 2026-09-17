#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of aurora.vsh (docs/vulkan-native-shaders.md).
// aurora.vsh declares shadowCoordsFar/Near itself under SHADOWQUALITY (it does not include shadowcoords.vsh)
// and never writes them. SHADOWQUALITY is a specialization constant, so they are declared unconditionally at
// the locations fogandlight.fsh reads, and stay unwritten as in GLSL 330.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"
#include "aurora.interface.glsl"

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
