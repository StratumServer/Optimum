#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkshadowmap.vsh (vanilla, docs/vulkan-native-shaders.md). USESSBO is the variant axis:
// 1 reads FaceData from set 2, 0 is the Chunkshadowmap_NoSSBOs registration with the per-vertex layout.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunkshadowmap.interface.glsl"

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
 #endif

layout(location = 0) out vec2 uv;

#include "vertexflagbits.glsl"
#include "vertexwarp.glsl"

 #if USESSBO > 0
layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexIndex / 4];
	int vIndex = gl_VertexIndex & 0x03;
	vec3 xyz = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #endif

	vec4 worldPos = vec4(xyz + origin, 1.0);
 #if USESSBO > 0
	worldPos = applyVertexWarping(vdata.flags[vIndex], worldPos);
 #else
	worldPos = applyVertexWarping(renderFlagsIn, worldPos);
 #endif
	worldPos = applyGlobalWarping(worldPos);

	gl_Position = mvpMatrix * worldPos;

 #if USESSBO > 0
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
        uv = uvIn;
 #endif

	// We could use this to fix peter panninng on tall grass, but needs an extra render pass or extra vertex data for grass
	//gl_Position.w += 1 * 0.00025 / max(0.1, gl_Position.z * 0.05);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
