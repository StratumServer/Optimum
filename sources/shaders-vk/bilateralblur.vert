#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of bilateralblur.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "bilateralblur.interface.glsl"

layout(location = 0) out vec2 texCoords[11];

void main(void)
{
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    vec2 texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

	if (isVertical == 1) {
		float pixelSize = 1.0 / frameSize.y;

		for (int i = -5; i < 5; i++) {
			texCoords[i + 5] = texCoord + vec2(0, pixelSize * i);
		}

	} else {
		float pixelSize = 1.0 / frameSize.x;

		for (int i = -5; i < 5; i++) {
			texCoords[i + 5] = texCoord + vec2(pixelSize * i, 0);
		}
	}

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
