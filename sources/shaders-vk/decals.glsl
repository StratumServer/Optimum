#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of decals.vsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axes: USESSBO (attribute layout and the FaceData buffer), TAAMOTION (the previous clip position).
// sources/shaders/decals.fsh explains why decals write the motion attachment themselves.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of decals.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
// The motion writer goes through include/motion.glsl with reactive 0 and writer depth gl_FragCoord.z; its
// behind-camera result vec4(0, 0, 0, 0) is the GLSL 330 vec4(0.0). GBUFFER is an axis only because it moves
// the motion attachment (TAAMOTIONLOCATION 4 with the G-buffer, 2 without).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of decals (docs/vulkan.md). Decals are pooled like chunks:
// the push block holds the two sampler slots (decals.fsh order) and the DRAW uniforms origin and
// modelViewMatrix (84 B). The record holds the rest: decals.vsh's, vertexwarp.vsh's previous-frame
// mirrors, then decals.fsh's, each in declaration order. The TAA uniforms are declared in every variant.
// The prev* initializers are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, decalTexture);
    OPTIMUM_SAMPLER_SLOT(sampler2D, blockTexture);
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogDensityIn;
    float fogMinIn;
    mat4 projectionMatrix;
    mat4 prevProjectionMatrix;
    mat4 prevModelViewMatrix;
    vec3 cameraPosDelta;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;

    vec2 taaRenderSize;
    vec2 taaJitterPx;
};

#if defined(OPTIMUM_VERTEX)


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

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 decalUv;
layout(location = 1) in vec2 blockUv;
layout(location = 2) in vec2 decalUvSize;
layout(location = 4) in vec4 color;
layout(location = 3) in vec2 decalUvStart;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

#if TAAMOTION == 1
layout(location = 5) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#endif

#include "motion.glsl"

void main()
{
	vec2 uv = vec2(decalUvStart.x + mod(decalUv.x, decalUvSize.x), decalUvStart.y + mod(decalUv.y, decalUvSize.y));

	outColor = color * texture(optimumTextures2D[decalTexture], uv);


	float blockAlpha = texture(optimumTextures2D[blockTexture], blockUv).a;
	if (outColor.a < 0.01 || blockAlpha < 0.01) discard;

	outGlow = vec4(0, 0, 0, outColor.a);

#if TAAMOTION == 1
	// b = 0: a decal overlays a static-or-swaying block surface and its vector is that surface's own.
	// a = gl_FragCoord.z, the depth the decal itself writes.
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, 0.0, gl_FragCoord.z);
#endif
}

#endif
