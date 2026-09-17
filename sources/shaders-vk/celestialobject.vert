#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of celestialobject.vsh (docs/vulkan-native-shaders.md). Axis: GBUFFER (fragPosition and
// gnormal, which GLSL 330 declares under SSAOLEVEL and never writes).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "celestialobject.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;


layout(location = 0) out vec3 vertexPosition;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out vec2 uv;
layout(location = 3) out vec4 color;
#if GBUFFER == 1
layout(location = 4) out vec4 fragPosition;
layout(location = 5) out vec4 gnormal;
#endif

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	vec4 worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
	vec4 cameraPos = viewMatrix * worldPos;
	uv = uvIn;
	glowLevel = (extraGlow + (flags & 0xff)) / 128.0;
	color = colorIn;
	color.a *= clamp(1 - getSpheresFogAmount(worldPos.xyz * 10), 0, 1);

	rgbaFog = rgbaFogIn;
	gl_Position = projectionMatrix * cameraPos;

	vertexPosition = worldPos.xyz;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
