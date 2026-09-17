#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of sky.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "sky.interface.glsl"

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
