#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of decals.vsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: USESSBO (attribute layout and the FaceData buffer), TAAMOTION (the previous clip position).
// sources/shaders/decals.fsh explains why decals write the motion attachment themselves.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "decals.interface.glsl"

 #if USESSBO == 1
layout(location = 0) in vec4 rgbaLightIn;
layout(location = 1) in vec2 blockUvIn;
layout(location = 2) in vec2 decalUvSizeIn;
layout(location = 3) in vec2 decalUvStartIn;
 #else
layout(location = 0) in vec3 vertexPos;
layout(location = 1) in vec2 decalUvIn;
// rgb = block light, a=sun light level
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in vec2 blockUvIn;
layout(location = 5) in vec2 decalUvSizeIn;
layout(location = 6) in vec2 decalUvStartIn; // Argh >.<
 #endif

layout(location = 0) out vec2 decalUv;
layout(location = 1) out vec2 blockUv;
layout(location = 2) out vec2 decalUvSize;
layout(location = 3) out vec2 decalUvStart;
layout(location = 4) out vec4 color;

#if TAAMOTION == 1
layout(location = 5) out vec4 taaPrevClip;
#endif

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

 #if USESSBO == 1
layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main () {
 #if USESSBO == 1
	FaceData vdata = faces[gl_VertexIndex / 4];
	int vIndex = gl_VertexIndex & 0x03;
	int renderFlagsIn = vdata.flags[vIndex];
	vec3 vertexPos = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #endif
	vec4 worldpos = vec4(vertexPos + origin, 1.0);

	worldpos = applyVertexWarping(renderFlagsIn, worldpos);
	worldpos = applyGlobalWarping(worldpos);

	vec4 cameraPos = modelViewMatrix * worldpos;

	gl_Position = projectionMatrix * cameraPos;


	color = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlagsIn, cameraPos);
	color = applyFog(worldpos, color, rgbaFogIn, fogMinIn, fogDensityIn);
	color.a = 1;

	// We pretend the decal is closer to the camera to enforce it
	// always being drawn on top
	//gl_Position.w += 0.0012; - not enough for leaves :o
	int zOffset = 1 + ((renderFlagsIn & ZOffsetBitMask) >> 8);
	gl_Position.w += zOffset * 0.00025 / max(0.1, gl_Position.z * 0.05);

 #if USESSBO == 1
	decalUv = UnpackUv(vdata, vIndex, 0, 0);
 #else
	decalUv = decalUvIn;
 #endif
	blockUv = blockUvIn;
	decalUvSize = decalUvSizeIn;
	decalUvStart = decalUvStartIn;

#if TAAMOTION == 1
	// The same vertex, one frame ago, through the same code path as the terrain under it, with the
	// "pretend the decal is closer" w-offset replayed so it is not reported as motion.
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = vec4(vertexPos + origin + cameraPosDelta, 1.0);
		taaPrevPos = applyVertexWarpingState(taaPrev, renderFlagsIn, taaPrevPos);
		taaPrevPos = applyGlobalWarpingState(taaPrev, taaPrevPos);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);

		int taaPrevZOffset = 1 + ((renderFlagsIn & ZOffsetBitMask) >> 8);
		taaPrevClip.w += taaPrevZOffset * 0.00025 / max(0.1, taaPrevClip.z * 0.05);
	}
#endif

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
