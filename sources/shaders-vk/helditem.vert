#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of helditem.vsh (docs/vulkan-native-shaders.md).
// SSAOLEVEL > 0 gates the G-buffer varyings, so it is the GBUFFER axis (section 5).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "helditem.interface.glsl"

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 modelColor;
layout(location = 3) in int flags;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;

layout(location = 2) out vec3 normal;
layout(location = 3) out vec3 vertexPosition;
#if GBUFFER == 1
layout(location = 4) out vec4 fragPosition;
layout(location = 5) out vec4 gnormal;
#endif



#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	vec4 cameraPos = modelViewMatrix * vec4(vertexPositionIn, 1.0);

	int glow = min(255, extraGlow + (flags & GlowLevelBitMask));
	glowLevel = glow / 255.0;

	uv = uvIn;
	color = applyLight(
		rgbaAmbientIn,
		rgbaLightIn,
		glow,
		cameraPos
	) * modelColor;

	color.rgb = mix(color.rgb, rgbaGlowIn.rgb, glow / 255.0 * rgbaGlowIn.a);

	gl_Position = projectionMatrix * cameraPos;

	normal = unpackNormal(flags);
	normal = normalize((modelViewMatrix * vec4(normal.x, normal.y, normal.z, 0)).xyz);

	#if GBUFFER == 1
		fragPosition = cameraPos;
		gnormal = vec4(normal, 0);
	#endif

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
