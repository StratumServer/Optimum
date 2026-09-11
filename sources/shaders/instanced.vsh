#version 330 core
// Optimum override of the vanilla instanced.vsh: adds the TAA motion-vector
// writer for the instanced mechanical-power renderers (P3) - axles, gears,
// clutches, transmissions, creative rotors and pulverizers. Everything else is
// vanilla, line for line.
//
// An instanced draw has no per-object uniforms to hang a previous transform off:
// every instance carries its own camera-relative mat4, rebuilt each frame, and
// the slot order changes as devices are added and removed. The previous
// transform therefore arrives the same way the current one does, as instance
// attributes in the same interleaved buffer (see OptimumInstanceMotion), with a
// metadata vec4 whose x says whether the C# side could match this instance to
// the same device in the previous frame. Without that match the vertex falls
// back to camera-only motion and the metadata's y raises reactive, so the
// resolve leans on this frame instead of reprojecting by another gear's matrix.
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPosition;  // Per vertex
layout(location = 1) in vec2 uvIn;				// Per vertex
layout(location = 2) in vec4 rgbaBlockIn;		// Per vertex (rgb = block light, a=sun light level)
layout(location = 3) in int renderFlagsIn; 	// Per vertex

layout(location = 4) in vec4 rgbaLightIn;		// Per instance
layout(location = 5) in mat4 transform;	 	// Per instance

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
layout(location = 9) in mat4 prevTransform;		// Per instance: this device's transform last frame
layout(location = 13) in vec4 taaInstanceMeta;	// Per instance: x = history usable, y = reactive
#endif

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;	
uniform float fogMinIn;
uniform float fogDensityIn;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

#if TAAMOTION > 0
uniform mat4 prevProjectionMatrix;   // previous frame's UNJITTERED projection for this draw's view
uniform mat4 prevModelViewMatrix;    // previous frame's CameraMatrixOrigin (the view instanced draws use)
uniform vec3 cameraPosDelta;         // cameraPos(this frame) - cameraPos(previous frame)
#endif

out vec4 color;
out vec2 uv;
out vec4 rgbaFog;
out float fogAmount;
out vec3 normal;
out vec4 worldPos;

#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif

flat out int renderFlags;

#if TAAMOTION > 0
out vec4 taaPrevClip;
out float taaInstanceReactive;
#endif

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh

void main()
{
	worldPos = transform * vec4(vertexPosition, 1.0);
	vec4 cameraPos = modelViewMatrix * worldPos;

#if TAAMOTION > 0
	// The same vertex, one frame ago. instanced.vsh applies no vertex warp and no
	// w-offset, so the previous position is the previous instance transform run
	// through the previous camera - nothing else has to be replayed.
	{
		vec4 taaPrevWorld;
		if (taaInstanceMeta.x != 0.0) {
			taaPrevWorld = prevTransform * vec4(vertexPosition, 1.0);
		} else {
			// Treat the block as static in the world: its camera-relative position
			// a frame ago differed by the camera's own movement only (accuracy rule 4).
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevWorld);
		taaInstanceReactive = taaInstanceMeta.y;
	}
#endif

	
	calcShadowMapCoords(modelViewMatrix, worldPos);
	
	uv = uvIn;
	color = applyLight(rgbaAmbientIn, rgbaLightIn * rgbaBlockIn, renderFlagsIn, cameraPos);
	rgbaFog = rgbaFogIn;
	
	// Distance fade out
	color.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5, -1, 1);
	gl_Position = projectionMatrix * cameraPos;
	
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	renderFlags = renderFlagsIn;
	
	normal = unpackNormal(renderFlagsIn);
	normal = normalize((transform * vec4(normal.x, normal.y, normal.z, 0)).xyz);

	#if SSAOLEVEL > 0
	fragPosition = cameraPos;
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	#endif
}
