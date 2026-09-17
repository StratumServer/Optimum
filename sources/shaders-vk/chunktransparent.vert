#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktransparent.vsh (vanilla, docs/vulkan-native-shaders.md). Axis: USESSBO (attribute
// layout and FaceData).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunktransparent.interface.glsl"

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in int colormapData;
 #endif



layout(location = 0) out vec4 rgba;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out float fogAmount;
layout(location = 3) out vec2 uv;
layout(location = 4) out vec4 worldPos;
layout(location = 5) out vec3 vertexPos;

layout(location = 6) flat out int renderFlags;
layout(location = 7) flat out vec3 normal;
layout(location = 8) out float normalShadeIntensity;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"
#include "colormap.vert.glsl"

 #if USESSBO > 0
layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexIndex / 4];
	int vIndex = gl_VertexIndex & 0x03;
	renderFlags = vdata.flags[vIndex];
	vec3 xyz = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;

 #endif

	vertexPos = xyz;

	vec4 truePos = vec4(xyz + origin, 1.0);
	worldPos = truePos;

	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

	calcShadowMapCoords(modelViewMatrix, worldPos);

 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = uvIn;
 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgba.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5 + max(0.0, worldPos.y * 0.02), -1.0, 1.0) - forcedTransparency;

	rgbaFog = rgbaFogIn;

	// To fix Z-Fighting on blocks over certain other blocks.
	if (gl_Position.z > 0) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / max(3, gl_Position.z * 0.05);
	}

	normal = unpackNormal(renderFlags);
	normalShadeIntensity = min(1, rgbaLightIn.a * 1.5);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
