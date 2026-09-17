#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of particlescube.vsh (the Optimum override in sources/shaders, docs/vulkan-native-shaders.md).
// Axes: VEC3SCALE (the scale attribute's type), TAAMOTION (the previous clip position), GBUFFER (the
// G-buffer varyings). sources/shaders/particlescube.vsh explains the camera-only previous position.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "particlescube.interface.glsl"

layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec4 normalv;				// Per vertex
layout (location = 2) in vec2 uv;						// Per vertex
layout (location = 3) in int renderFlags; 			// Per instance

layout (location = 4) in vec3 particlePosition; 	// Per instance (=per particle)
#if VEC3SCALE == 1
layout (location = 5) in vec3 scale;					// Per instance
#else
layout (location = 5) in float scale;					// Per instance
#endif
layout (location = 6) in vec4 particleDir; 			// Per instance
layout (location = 7) in vec4 rgbaLightIn; 		// Per instance
layout (location = 8) in vec4 rgbaBlockIn; 		// Per instance

layout(location = 0) out vec4 color;
layout(location = 1) out vec4 rgbaFog;
layout(location = 2) out vec3 normal;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec4 worldPos;

#if TAAMOTION == 1
layout(location = 5) out vec4 taaPrevClip;
#endif
#if GBUFFER == 1
layout(location = 6) out vec4 fragPosition;
layout(location = 7) out vec4 gnormal;
#endif

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

#define M_PI 3.1415926535897932384626433832795

mat4 rotation3d(vec3 axis, float angle) {
  axis = normalize(axis);
  float s = sin(angle);
  float c = cos(angle);
  float oc = 1.0 - c;

  return mat4(
    oc * axis.x * axis.x + c,           oc * axis.x * axis.y - axis.z * s,  oc * axis.z * axis.x + axis.y * s,  0.0,
    oc * axis.x * axis.y + axis.z * s,  oc * axis.y * axis.y + c,           oc * axis.y * axis.z - axis.x * s,  0.0,
    oc * axis.z * axis.x - axis.y * s,  oc * axis.y * axis.z + axis.x * s,  oc * axis.z * axis.z + c,           0.0,
    0.0,                                0.0,                                0.0,                                1.0
  );
}

float atan2(in float y, in float x)
{
    bool s = (abs(x) > abs(y));
    return mix(M_PI/2.0 - atan(x,y), atan(y,x), s);
}


#if TAAMOTION == 1
// The position half of main() below, as a function of the warp state and the particle's position, so the
// same code is evaluated for this frame and for the previous one.
vec4 taaParticleWorldPos(WarpState st, vec3 taaParticlePosition)
{
	vec4 taaWorldPos;
#if VEC3SCALE == 1
	mat4 rotMat = rotation3d(vec3(0,1,0), atan2(particleDir.z, particleDir.x) + particleDir.w);
	taaWorldPos = rotMat * (vec4(vertexPosition,1.0) * vec4(scale,1.0)) + vec4(taaParticlePosition, 1.0);
	taaWorldPos.w=1;
#else
	taaWorldPos = vec4(vertexPosition * scale + taaParticlePosition, 1.0);
#endif

	taaWorldPos = applyVertexWarpingState(st, renderFlags, taaWorldPos);
	taaWorldPos = applyGlobalWarpingState(st, taaWorldPos);
	return taaWorldPos;
}
#endif


void main()
{
#if VEC3SCALE == 1
	mat4 rotMat = rotation3d(vec3(0,1,0), atan2(particleDir.z, particleDir.x) + particleDir.w);
	worldPos = rotMat * (vec4(vertexPosition,1.0) * vec4(scale,1.0)) + vec4(particlePosition, 1.0);
	worldPos.w=1;
#else
	worldPos = vec4(vertexPosition * scale + particlePosition, 1.0);
#endif

	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);
	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

	int flags = min(255, 2 * (renderFlags & 0xff)); // increase the glow on cube particles
	color = applyLight(rgbaAmbientIn, rgbaLightIn, flags, cameraPos) * rgbaBlockIn;

	fogAmount = getFogLevel(vec4(particlePosition, 0), fogMinIn, fogDensityIn);
	rgbaFog = rgbaFogIn;
	normal = normalv.xyz;

	calcShadowMapCoords(modelViewMatrix, worldPos);

#if GBUFFER == 1

	fragPosition = cameraPos;
	gnormal = modelViewMatrix * vec4(normal.xyz, 0.25);
#endif

#if TAAMOTION == 1
	// The same vertex, one frame ago: the particle where it is now, moved by exactly the camera's own
	// motion, the warp re-evaluated with the previous frame's counters, and the previous UNJITTERED
	// projection with the previous CameraMatrixOrigin.
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = taaParticleWorldPos(taaPrev, particlePosition + cameraPosDelta);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);
	}
#endif

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
