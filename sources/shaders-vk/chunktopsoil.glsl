#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktopsoil.vsh (the Optimum override in sources/shaders, docs/vulkan.md).
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
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunktopsoil.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axes: GBUFFER (G-buffer outputs; the motion output moves from 2 to 4 with it), TAAMOTION (motion output,
// written through include/motion.glsl). SHADOWQUALITY, NORMALVIEW and SHINYEFFECT are
// specialization-constant branches.
//
// Optimum override of the vanilla chunktopsoil.fsh: adds the TAA motion-vector
// output (P3). Everything else is vanilla, line for line.
//
// fogandlight.frag.glsl and fogspheres.glsl read names owned by fogandlight.vsh and vertexwarp.vsh, which the
// vertex stage includes, so this stage activates those owners' names itself (contract section 3).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunktopsoil (docs/vulkan.md). One draw per mesh pool: the
// push block holds the two sampler slots in chunktopsoil.fsh's declaration order, then origin and
// modelViewMatrix (84 B). The record holds every other uniform, chunktopsoil.vsh's first (the previous-frame
// warp state vertexwarp.glsl reads comes with its include), then chunktopsoil.fsh's and underwatereffects'
// frameSize. Every uniform is declared whatever the axes: the GLSL 330 name set does not depend on defines.
//
// A block member cannot carry chunktopsoil.fsh's initializer (alphaTest = 0.01); the runtime seeds the record
// from the GLSL 330 declarations (contract section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTexLinear);
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
    float subpixelPaddingX;
    float subpixelPaddingY;
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

    float alphaTest;
    vec2 blockTextureSize;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


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

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) in vec4 rgba;
layout(location = 1) in vec4 rgbaFog;
layout(location = 2) in float fogAmount;
layout(location = 3) in vec2 uv;
layout(location = 4) in vec2 uv2;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
layout(location = 9) in vec4 worldPos;
layout(location = 8) in vec3 vertexPosition;

layout(location = 10) flat in int renderFlags;
layout(location = 5) in vec3 normal;
#if GBUFFER == 1
layout(location = 7) in vec4 gnormal;
#endif



layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 6) in vec4 fragPosition;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

// TAA motion vectors (Optimum P3); see chunkopaque.frag for the contract.
#if TAAMOTION > 0
layout(location = 11) in vec4 taaPrevClip;
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "colormap.frag.glsl"
#include "noise3d.glsl"
#include "underwatereffects.glsl"

void main()
{
	vec4 brownSoilColor = texture(optimumTextures2D[terrainTex], uv) * rgba;

        if (normal.y >= 0) {
             // Top (normal.y == 1) or Sides (normal.y == 0)
            vec4 grassColor = getColorMapped(optimumTextures2D[terrainTexLinear], texture(optimumTextures2D[terrainTex], uv2 + vec2(blockTextureSize.x * normal.y, 0))) * rgba;
            outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
        } else {
             // Bottom
            outColor = applyFog(brownSoilColor, fogAmount);
	}

	if (psychedelicStrength > Epsilon) outColor = applyPsychedelicEffect(outColor, vertexPosition*2, 0);
	if (glitchStrength > Epsilon) outColor = applyRustEffect(outColor, normal, vertexPosition, 1);


	// Declared before the branch that assigns it (contract section 5); every path overwrites the 0.45.
	float intensity = 0.45;
	if (OPTIMUM_SHADOWQUALITY > 0) {
	intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	} else {
	intensity = 0.45;
	}



	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowWithNormal(outColor, clamp(fogAmount - 50*murkiness, 0, 1), normal, 1, intensity, worldPos.xyz);
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

	outColor.a = rgbaFog.a;

	float aTest = outColor.a;
	aTest += max(0.0, 1 - rgba.a) * min(1, outColor.a * 10);
	if (OPTIMUM_NORMALVIEW == 0) {
	 // Fade to sky color
         // Also, when looking through tinted glass you can clearly see the edges where we fade to sky color; using the outColor.a < 0.005 discard seems to completely fix that
	if (aTest < alphaTest || outColor.a < 0.005) discard;
	}


	float glow = 0;

	if (OPTIMUM_SHINYEFFECT > 0) {
	if ((renderFlags & ReflectiveBitMask) > 0) {
		vec3 worldVec = normalize(worldPos.xyz);

		float angle = 2 * dot(normalize(normal), worldVec);
		angle += gnoise(vec3(uv.x*500, uv.y*500, worldVec.z/10)) / 7.5;
		outColor.rgb *= max(vec3(1), vec3(1) + 3*blockLight * gnoise(vec3(worldVec.x/10 + angle, worldVec.y/10 + angle, worldVec.z/10 + angle)));
	}

	glow = pow(max(0.0, dot(normal, lightPosition)), 6) * 0.1 * shadowIntensity * (1 - fogAmount);
	}



#if GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount * 2 + glowLevel);
	outGNormal = gnormal;
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

    outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);
#if TAAMOTION > 0
	// Opaque terrain is not reactive.
	outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, 0.0, gl_FragCoord.z);
#endif
}

#endif
