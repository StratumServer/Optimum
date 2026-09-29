#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkshadowmap.vsh (vanilla, docs/vulkan.md). USESSBO is the variant axis:
// 1 reads FaceData from set 2, 0 is the Chunkshadowmap_NoSSBOs registration with the per-vertex layout.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkshadowmap.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunkshadowmap (docs/vulkan.md). One draw per mesh pool
// per cascade: the push block holds the sampler slot, then origin and mvpMatrix (80 B). The record holds
// the subpixel padding and the previous-frame warp state vertexwarp.glsl reads.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
    vec3 origin;
    mat4 mvpMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float subpixelPaddingX;
    float subpixelPaddingY;

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
};

#if defined(OPTIMUM_VERTEX)


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

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main () {
	outColor = texture(optimumTextures2D[tex2d], uv);
	// Optimum: raise discard threshold from 0.02 to 0.15.
	// Skips more near-transparent shadow fragments (grass edges, leaf fringes)
	// with no visible shadow quality loss at typical view distances.
	if (outColor.a < 0.15) discard;

}

#endif
