#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of taa-skymotion.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "taa-skymotion.interface.glsl"

// Optimum TAA (P4): the vertex half of the sky/cloud motion pass.
//
// A fullscreen triangle generated from gl_VertexID, exactly like the other
// Optimum post passes (taa-resolve, fsr-easu), with one difference that is the
// whole point of the pass: gl_Position.z equals gl_Position.w, so the NDC depth
// is 1 and the window depth is 1.0 - the far plane, which is the value Primary's
// depth attachment still holds wherever no geometry was drawn. The depth remap
// at the end keeps that: (1 + 1) * 0.5 = 1 = w.
//
// With the depth test on and GL_LEQUAL the triangle therefore passes on sky
// pixels only (1.0 <= 1.0) and is rejected by every pixel any surface wrote
// depth for (depth < 1.0). That is what keeps this pass from overwriting the
// motion vectors the terrain, entity, liquid and particle writers already put
// down. The pass never writes depth itself.

layout(location = 0) out vec2 texCoord;

void main(void)
{
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
	float y = -1.0 + float((gl_VertexIndex & 2) << 1);
	gl_Position = vec4(x, y, 1.0, 1.0);
	texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);

	// GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
