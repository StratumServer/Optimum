#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guigear.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guigear.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of guigear (docs/vulkan.md, GUI row): the sampler slot in the
// push block; the matrices and guigear.fsh's scalars in the record, each stage in declaration order.
// stabilityLevel's GLSL 330 initializer (0.5) is seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float gearCounter;
    float stabilityLevel;
    float shadeYPos;
    float hotbarYPos;
    float gearHeight;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;


layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 pos;

void main(void)
{
	gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);

	uv = uvIn;
	pos = (modelViewMatrix * vec4(vertexPositionIn, 1.0)).xy;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;
layout(location = 1) in vec2 pos;

layout(location = 0) out vec4 outColor;

#include "noise3d.glsl"

void main () {
	outColor = texture(optimumTextures2D[tex2d], uv);
	if (outColor.a <= 0.01) discard;

	vec3 tealCol = vec3(56/255.0, 232/255.0, 182/255.0);
	vec3 tealDarkCol = vec3(80/255.0, 98/255.0, 93/255.0);

	float noise = abs(gnoise(vec3(pos.x/20, pos.y/20, gearCounter * 0.75)));
	float height = gearHeight - 10;

	float relY = (pos.y - hotbarYPos + height * 0.685) / height; // No idea why the 0.68 and not 0.55

	float ya = 1 - relY;
	float yb = 0.5 + stabilityLevel/2 + noise/30.0;

	if (ya < yb) {
		float b = 1.2f * (max(0.0, 0.4 - (outColor.r + outColor.g + outColor.b) / 3) + noise/5);

		outColor.rgb = mix(outColor.rgb, tealCol - b, clamp((yb - ya)*50, 0, 1));
	}

	outColor.rgb *= 1 - max(0.0, pos.y-shadeYPos)/15;
	outColor.a *= 1;//0.5;

	//outColor.rgb = vec3(relY);

}

#endif
