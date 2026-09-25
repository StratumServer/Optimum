#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guitopsoil.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guitopsoil.fsh (docs/vulkan.md).
// color and glowLevel are declared by the GLSL 330 stage but written by no vertex stage and read by nothing;
// they keep their declarations (glowLevel at its shared varying location), and the optimised module drops them.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of guitopsoil (docs/vulkan.md, GUI row): the sampler slot, then
// the per-element GUI uniforms (rgbaIn, extraGlow, applyColor, noTexture) in the push block; the matrices,
// blockTextureSize and alphaTest in the record, each stage in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    vec4 rgbaIn;
    int extraGlow;
    int applyColor;
    float noTexture;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float blockTextureSize;
    float alphaTest;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 uv2;
layout(location = 2) out vec4 rgba; // Without biome tint
layout(location = 3) out vec4 rgba2; // With biome tint


void main(void)
{
	uv = uvIn;
    uv2 = uvIn;
    rgba = vec4(1);
    rgba2 = vec4(1);
	float glowLevel = extraGlow / 128.0;

	vec4 color = rgbaIn * (1 + glowLevel);
	if (applyColor == 1) color *= colorIn;

	gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 2) in vec4 rgba; // Without biome tint
layout(location = 3) in vec4 rgba2; // With biome tint
layout(location = 0) in vec2 uv;
layout(location = 1) in vec2 uv2;
layout(location = 4) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;

layout(location = 0) out vec4 outColor;

void main () {

	vec4 brownSoilColor = texture(optimumTextures2D[terrainTex], uv) * rgba;
	vec4 grassColor;

	if (rgba.a < 0.01) {
		// Bottom
		outColor = brownSoilColor;
	} else {
		if (rgba2.a > 0.01) {
			// Top
			grassColor = texture(optimumTextures2D[terrainTex], uv2) * rgba2;
		} else {
			// Side + Overlay
			grassColor = texture(optimumTextures2D[terrainTex], uv2 + vec2(blockTextureSize, 0)) * vec4(rgba2.rgb, 1);
		}

		outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
	}
	outColor.a = 1;
}

#endif
