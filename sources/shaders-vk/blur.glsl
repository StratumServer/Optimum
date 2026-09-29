#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blur.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blur.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of blur (docs/vulkan.md). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slot and blur.vsh's two uniforms are the record.
//
// blur.fsh also declares an input named frameSize that no vertex stage writes and nothing reads. The
// record's frameSize is a global name in both stages, so that input would redeclare it: the fragment stage
// drops it (docs/vulkan.md, "Family post"). texCoords[21] spans locations 0-20; the program
// includes none of the includes varyings.glsl places at 16 and above.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, inputTexture);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 frameSize;
    int isVertical;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) out vec2 texCoords[21];

void main(void)
{
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    vec2 texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

	if (isVertical == 1) {
		float pixelSize = 1.0 / frameSize.y;

		for (int i = -8; i < 8; i++) {
			texCoords[i + 8] = texCoord + vec2(0, pixelSize * i);
		}

	} else {
		float pixelSize = 1.0 / frameSize.x;

		for (int i = -8; i < 8; i++) {
			texCoords[i + 8] = texCoord + vec2(pixelSize * i, 0);
		}
	}

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


// blur.fsh's `in vec2 frameSize;` is dropped: see blur.interface.glsl.
layout(location = 0) in vec2 texCoords[21];

layout(location = 0) out vec4 outColor;

// http://dev.theomader.com/gaussian-kernel-calculator/
void main(void)
{
	vec4 out_colour = vec4(0.0);
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[0]) * 0.001422;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[1]) * 0.004255;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[2]) * 0.011001;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[3]) * 0.024574;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[4]) * 0.047431;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[5]) * 0.0791;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[6]) * 0.113978;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[7]) * 0.141908;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[8]) * 0.152663;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[9]) * 0.141908;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[10]) * 0.113978;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[11]) * 0.0791;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[12]) * 0.047431;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[13]) * 0.024574;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[14]) * 0.011001;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[15]) * 0.004255;
	out_colour += texture(optimumTextures2D[inputTexture], texCoords[16]) * 0.001422;

	outColor = out_colour;
}

#endif
