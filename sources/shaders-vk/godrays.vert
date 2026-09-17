#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of godrays.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "godrays.interface.glsl"

layout(location = 0) out vec2 texCoord;
layout(location = 1) out vec3 sunPosScreen;
layout(location = 2) out float iGlobalTime;
layout(location = 3) out float intensity;
layout(location = 4) out float direction;

void main(void)
{
	// https://randallr.wordpress.com/2014/06/14/rendering-a-screen-covering-triangle-in-opengl
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

	sunPosScreen = sunPosScreenIn;
	iGlobalTime = iGlobalTimeIn;

	float sunPlrAngle = (dot(sunPos3dIn, playerViewVector) + 1) / 3;

	direction = dot(sunPos3dIn, playerViewVector) >= 0 ? 1 : -1;

	// https://www.toolfk.com/online-plotter-frame/#W3sidHlwZSI6MCwiZXEiOiJtYXgoMSwxLjc1KigxLTYqYWJzKHgtMC4yMikpKSIsImNvbG9yIjoiIzAwMDAwMCJ9LHsidHlwZSI6MTAwMCwid2luZG93IjpbIi0xIiwiMSIsIjAiLCIyIl19XQ--
	float dawnMul = max(1, (1-dusk) * 2 * (1 - 6*abs(sunPos3dIn.y - 0.1)));

	// Intensity is determined by how directly the player is looking at the sun
	// above intensity 0.8 we get godrays where they shouldn't be o.o
	intensity = clamp(sunPlrAngle * dawnMul, 0, 0.8);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
