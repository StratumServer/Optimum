#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktopsoil.vsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: USESSBO (attribute layout and FaceData), GBUFFER (fragPosition, gnormal), TAAMOTION (taaPrevClip).
//
// Optimum override of the vanilla chunktopsoil.vsh: adds the TAA motion-vector
// writer (P3). Everything else is vanilla, line for line.
//
// Topsoil deliberately does NOT call applyVertexWarping - vanilla has that call
// commented out and only applies the global warp - so the previous position
// must reproduce exactly that asymmetry, not the chunkopaque path.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "chunktopsoil.interface.glsl"

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
layout(location = 1) in vec2 uv2In;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in vec2 uv2In;
layout(location = 5) in int colormapData;
 #endif


layout(location = 0) out vec4 rgba;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out float fogAmount;
layout(location = 3) out vec2 uv;
layout(location = 4) out vec2 uv2;
layout(location = 5) out vec3 normal;

 #if GBUFFER == 1
layout(location = 6) out vec4 fragPosition;
layout(location = 7) out vec4 gnormal;
 #endif

layout(location = 8) out vec3 vertexPosition;
layout(location = 9) out vec4 worldPos;

layout(location = 10) flat out int renderFlags;

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
layout(location = 11) out vec4 taaPrevClip;
#endif


#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"
#include "colormap.vert.glsl"

 #if USESSBO > 0
layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif

const float uvEpsilon = 1.0 / 32768.0;

void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexIndex / 4];
	int vIndex = gl_VertexIndex & 0x03;
	renderFlags = vdata.flags[vIndex];
	vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	vertexPosition = xyz;
 #endif

	vec4 truePos = vec4(vertexPosition + origin, 1.0);
	worldPos = truePos;
	//worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

#if TAAMOTION > 0
	// The same vertex, one frame ago: camera-relative position displaced by the
	// camera's own motion (accuracy rule 4), the global warp re-evaluated with
	// the previous frame's counters - and no vertex warp, matching the vanilla
	// path above - through the previous unjittered projection.
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);
		taaPrevPos = applyGlobalWarpingState(taaPrev, taaPrevPos);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);
	}
#endif

	calcShadowMapCoords(modelViewMatrix, worldPos);

 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = uvIn;
 #endif
	uv2 = uv2In * 2.0 - vec2((int(uv2In.x * 0x10000) & 1) * (uvEpsilon + subpixelPaddingX * 2.0), (int(uv2In.y * 0x10000) & 1) * (uvEpsilon + subpixelPaddingY * 2.0));  // uv2In least significant bit is a flag which tells whether this coordinate is (for .x) u1 or u2, or (for .y) v1 or v2

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;

	rgbaFog.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5 + max(0.0, worldPos.y * 0.02), 0.0, 1.0);

	normal = unpackNormal(renderFlags);

#if GBUFFER == 1
	fragPosition = cameraPos;
	gnormal = modelViewMatrix * vec4(normal, 0);
	gnormal.w=0;
#endif

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
