#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in int colormapData;
 #endif

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform float cameraUnderwater;

uniform float shadowIntensity = 1;
uniform vec3 lightPosition;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

out vec4 rgba;
out vec2 uv;
out vec4 rgbaFog;
out float fogAmount;
out vec3 normal;
out vec3 vertexPosition;
out vec4 worldPos;
out vec4 camPos;
out float lod0Fade;
out float nb;

// Greedy mesh tile repeat (Optimum): tile counts and sub-texture bounds.
// Compiled in only when the feature is on (GREEDYMESH stamped from
// OptimumConfig at shader load); at 0 this whole shader preprocesses to
// vanilla, so disabled greedy meshing costs nothing.
#if GREEDYMESH > 0
flat out int tileWidth;
flat out int tileHeight;
flat out vec2 tileBoundsMin;
flat out vec2 tileBoundsSize;
#endif

 #if SSAOLEVEL > 0
out vec4 gnormal;
 #endif


flat out int renderFlags;

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh
#include colormap.vsh

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexID / 4];
	int vIndex = gl_VertexID & 0x03;
	renderFlags = vdata.flags[vIndex];
	vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	vertexPosition = xyz;
 #endif

#if GREEDYMESH > 0
	// Decode greedy tile counts from flags (Optimum).
	// Bit 11 (ReflectiveBitMask) is the sentinel: set only on greedy-
	// tiled quads (eligible blocks are never reflective). When set,
	// bits 29-31 = tileWidth - 1, bits 8-10 = tileHeight - 1.
	// When clear, this is a vanilla vertex: do not touch those bits.
	int greedyTiled = (renderFlags >> 11) & 1;
	if (greedyTiled != 0) {
		tileWidth = ((renderFlags >> 29) & 0x7) + 1;
		tileHeight = ((renderFlags >> 8) & 0x7) + 1;
		// Clear sentinel + tile bits so downstream code (normal unpack,
		// wind, zoffset) does not misinterpret them.
		renderFlags = renderFlags & ~(0x7 << 29) & ~(0x7 << 8) & ~(1 << 11);
	} else {
		tileWidth = 1;
		tileHeight = 1;
	}
#endif

	vec4 truePos = vec4(vertexPosition + origin, 1.0);
	bool isLeaves = ((renderFlags & WindModeBitMask) > 0); 
	
	worldPos = applyVertexWarping(renderFlags, truePos);
	worldPos = applyGlobalWarping(worldPos);
	
	camPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * camPos;
	
	calcShadowMapCoords(modelViewMatrix, worldPos);
 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);

#if GREEDYMESH > 0
	// Extract tile sub-texture bounds from FaceData for the fragment
	// shader's tiling wrap. vdata.uv is the origin (vertex 0 UV packed
	// as 16-bit fixed point), vdata.uvSize carries the delta to vertex 2.
	// Unpack to float atlas coords matching UnpackUv's scale.
	if (greedyTiled != 0) {
		tileBoundsMin = vec2(vdata.uv & 0xFFFF, vdata.uv >> 16 & 0xFFFF) / 32768.0;
		int uvs = vdata.uvSize;
		// Preserve sign: negative delta means the axis is inverted
		// (vertex 0 sits at the high end, vertex 2 at the low end).
		// The fragment shader needs this to map fract(position) correctly.
		tileBoundsSize = vec2(
			(uvs & 0x7FFF) - ((uvs & 0x4000) << 1),
			(uvs >> 16 & 0x7FFF) - ((uvs & 0x40000000) >> 15)
		) / 32768.0;
	} else {
		tileBoundsMin = vec2(0.0);
		tileBoundsSize = vec2(0.0);
	}
#endif
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = uvIn;

#if GREEDYMESH > 0
	// GL 3.3 path has no channel to carry tile bounds (would need an extra
	// vertex attribute via CustomFloats, which the opaque pass's MeshData
	// doesn't allocate). The emitter (OptimumGreedyMeshEmitter) forces
	// 1x1-only merges whenever UseSSBOs is false, so greedyTiled should
	// never be set here - this just forces the tile count back to 1x1 as
	// a second guard, so a merged quad can never reach the fragment
	// shader's tileBoundsSize division (which would be 0/0 = NaN) even if
	// that invariant is ever violated (e.g. SSBOs toggled mid-session
	// before chunks retesselate).
	if (greedyTiled != 0) {
		tileWidth = 1;
		tileHeight = 1;
	}
	tileBoundsMin = vec2(0.0);
	tileBoundsSize = vec2(0.0);
#endif
 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);
	
	// Distance fade out
	rgba.a = clamp(17.0 - 20.0 * length(worldPos.xz) / viewDistance + max(0.0, worldPos.y * 0.02), -1.0, 1.0);
	
	rgbaFog = rgbaFogIn;
	
	normal = unpackNormal(renderFlags);
	
#if SSAOLEVEL > 0
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	gnormal.w = isLeaves ? 1 : 0;
#endif


	// To fix Z-Fighting on blocks over certain other blocks
	if (gl_Position.z > -1) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / ((gl_Position.z + 3) * 0.05);
	}
	

	if ((renderFlags & Lod0BitMask) != 0) {
		float b = clamp(10 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 2.5, 0.0, 1.0);
		lod0Fade = 1 - b;
	}
	else    lod0Fade = 0.0;
	

#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0;
#else
	float intensity = 0.45;
#endif
	nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);
}
