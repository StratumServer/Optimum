#version 330 core

// Optimum TAA (P4): the vertex half of the sky/cloud motion pass.
//
// A fullscreen triangle generated from gl_VertexID, exactly like the other
// Optimum post passes (taa-resolve, fsr-easu), with one difference that is the
// whole point of the pass: gl_Position.z equals gl_Position.w, so the NDC depth
// is 1 and the window depth is 1.0 - the far plane, which is the value Primary's
// depth attachment still holds wherever no geometry was drawn.
//
// With the depth test on and GL_LEQUAL the triangle therefore passes on sky
// pixels only (1.0 <= 1.0) and is rejected by every pixel any surface wrote
// depth for (depth < 1.0). That is what keeps this pass from overwriting the
// motion vectors the terrain, entity, liquid and particle writers already put
// down. The pass never writes depth itself.

out vec2 texCoord;

void main(void)
{
	float x = -1.0 + float((gl_VertexID & 1) << 2);
	float y = -1.0 + float((gl_VertexID & 2) << 1);
	gl_Position = vec4(x, y, 1.0, 1.0);
	texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
}
