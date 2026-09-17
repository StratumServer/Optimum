#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of final.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "final.interface.glsl"

layout(location = 0) out vec2 texCoord;
layout(location = 1) out vec2 invFrameSize;
layout(location = 2) flat out float godrayIntensity;

void main(void)
{
	// https://rauwendaal.net/2014/06/14/rendering-a-screen-covering-triangle-in-opengl/
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

	invFrameSize = invFrameSizeIn;

	// Copied from godrays.vsh, should be a #include
	float sunPlrAngle = 0.5 + 0.25 * (dot(sunPos3dIn, playerViewVector) + 1);
	float dawnDuskMul = max(1, 1.75 * (1 - 6*abs(sunPos3dIn.y - 0.22)));
	float nightFade = max(0.0, -2*sunPos3dIn.y + 0.4);

	// Intensity is determined by how directly the player is looking at the sun
	godrayIntensity = max(0.0, sunPlrAngle * dawnDuskMul - nightFade) / 2;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
