#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquid.vsh (vanilla, docs/vulkan.md). No variant axes in this stage.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of chunkliquid.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
// Axis: USEOIT, through include/oit.glsl, which declares the six OIT outputs and OIT() only when it is 1.
// The client registers chunkliquid with Oit = true (ShaderProgramBase's default), so USEOIT=0 is never
// linked; the GLSL 330 program has no outputs and no OIT() there either, so the 0 variant skips the call and
// writes nothing. FOAMEFFECT and SHADOWQUALITY are specialization-constant branches.
//
// waterWaveCounter, windSpeed and the names fogandlight.frag.glsl and fogspheres.glsl read belong to
// vertexwarp.vsh and fogandlight.vsh, which the vertex stage includes, so this stage activates those owners'
// names itself (contract section 3).
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of chunkliquid (docs/vulkan.md). One draw per mesh pool: the
// push block holds the two sampler slots in chunkliquid.fsh's declaration order, then origin and
// modelViewMatrix (84 B). The record holds every other uniform, chunkliquid.vsh's first (the previous-frame
// warp state vertexwarp.glsl reads comes with its include), then chunkliquid.fsh's and underwatereffects'
// frameSize. blockTextureSize and sunPosRel, declared by both stages, appear once.
//
// waterWaveCounter and windSpeed, which chunkliquid.fsh declares itself, are frame members here (their owner
// vertexwarp.vsh is included by the vertex stage). A block member cannot carry chunkliquid.fsh's initializer
// (dropletIntensity = 0); the runtime seeds the record from the GLSL 330 declarations (contract section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    vec3 origin;
    mat4 modelViewMatrix;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float waterStillCounter;
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogDensityIn;
    float fogMinIn;
    mat4 projectionMatrix;
    vec2 blockTextureSize;
    vec3 playerViewVec;
    vec3 sunPosRel;
    vec3 playerPosForFoam;
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

    vec2 textureAtlasSize;
    float waterFlowCounter;
    vec3 sunColor;
    vec3 reflectColor;
    float sunSpecularIntensity;
    float dropletIntensity;

    vec2 frameSize;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlags;   // Check out chunkvertexflags.ash for understanding the contents of this data
layout(location = 4) in vec2 flowVector;
layout(location = 5) in int colormapData;

// Bit 0: Should animate yes/no
// Bit 1: Should texture fade yes/no
// Bit 2-9: Oceanity
// Bits 10-17: x-Distance to upper left corner, where 255 = size of the block texture
// Bits 18-26: y-Distance to upper left corner, where 255 = size of the block texture
// Bit 27: Lava yes/no - use LiquidIsLavaBitPosition

// Bit 28: Weak foamy yes/no - use LiquidWeakFoamBitPosition
// Bit 29: Weak Wavy yes/no - use LiquidWeakWaveBitPosition
// Bit 30: Don't tweak alpha channel - use LiquidFullAlphaBitPosition
// Bit 31: LiquidExposedToSky - use LiquidSkyExposedBitPosition

layout(location = 6) in int waterFlagsIn;

layout(location = 0) out vec2 flowVectorf;
layout(location = 1) out vec4 rgba;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec2 uv;
layout(location = 5) out vec2 uvSize;
layout(location = 6) out float waterStillCounterOff;
layout(location = 7) out vec3 fragWorldPos;
layout(location = 8) out vec3 fragNormal;
layout(location = 9) out vec3 fWorldPos;
layout(location = 10) out float fresnel;
layout(location = 11) flat out int skyExposed;

layout(location = 12) flat out vec2 uvBase;
layout(location = 13) flat out int waterFlags;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"
#include "colormap.vert.glsl"


void main(void)
{
	vec4 truePos = vec4(xyz + origin, 1.0);
	vec4 worldPos = truePos;

	if ((waterFlagsIn & 1) == 1) {
		float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) ? 90 : 5;

		float oceanity = ((waterFlagsIn >> 2) & 0xff) * OneOver255;
		div *= max(0.2, 1 - oceanity);

		worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div);
	}
	else if ((waterFlagsIn & LiquidWeakWaveBitMask) > 0) {
		worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, 90);
	}

	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;

	float x = mod(waterStillCounter + length(worldPos.xz + playerpos.xz) * 0.3333333, 2);

	waterStillCounterOff = smoothstep(0, 1, abs(x - 1));
	if ((waterFlagsIn & 2) == 0) {
		waterStillCounterOff = 1;
	}


	fragWorldPos = worldPos.xyz + playerPosForFoam;
	fWorldPos = worldPos.xyz;

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

        uv = uvIn;

	uvSize = vec2((waterFlagsIn >> 10) & 0xff, (waterFlagsIn >> 18) & 0xff) * OneOver255 * blockTextureSize;
	uvBase = uv - uvSize;

	flowVectorf = flowVector;

	waterFlags = waterFlagsIn;
	fragNormal = unpackNormal(renderFlags);
	skyExposed = (renderFlags >> LiquidSkyExposedBitPosition) & 1;



	vec3 eyeFresnel = normalize(vec3(worldPos.x, worldPos.y - 3.5, worldPos.z));
	float bias = 0.01;
	float scale = 4.5;
	float power = 3.0;

	if ((renderFlags & GlowLevelBitMask) == 0) { // Don't apply to glowing liquids for now, looks weird on lava (makes it less glowy)
		fresnel = max(0.2, bias + scale * pow(1 + dot(eyeFresnel, fragNormal), power));

		fresnel = min(fresnel, clamp(20 * (1.05 - length(worldPos.xz) / viewDistance) - 5 + max(0.0, worldPos.y * 0.02), -1.0, 1.5));

		rgba.a = clamp(0.8*fresnel, 0, 2);

		if (fragNormal.y < 0.5) {
			rgba.a *= 0.3333333;
		}
	}


	calcShadowMapCoords(modelViewMatrix, worldPos);
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);

	// We pretend the decal is closer to the camera to enforce it always being drawn on top
	// Required e.g. when water is besides stairs or slabs
	gl_Position.w += 0.0008 / max(0.1, gl_Position.z);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 1) in vec4 rgba;
layout(location = 2) in vec4 rgbaFog;
layout(location = 3) in float fogAmount;
layout(location = 4) in vec2 uv;
layout(location = 5) in vec2 uvSize;
layout(location = 6) in float waterStillCounterOff;
layout(location = 12) flat in vec2 uvBase;
layout(location = 7) in vec3 fragWorldPos;
layout(location = 9) in vec3 fWorldPos;
layout(location = 8) in vec3 fragNormal;
layout(location = 10) in float fresnel;
layout(location = 11) flat in int skyExposed;

layout(location = 0) in vec2 flowVectorf;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;

layout(location = 13) flat in int waterFlags;
// Declared and never written by chunkliquid.vsh or read here, as in the GLSL 330 stage.
layout(location = 14) flat in int renderFoam;

#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "colormap.frag.glsl"
#include "underwatereffects.glsl"
#include "oit.glsl"


vec2 droplethash3( vec2 p )
{
    //vec2 q = vec2(dot(p,vec2(127.1,311.7)), dot(p,vec2(269.5,183.3))); - causes too high values and weird distortions

	vec2 q = vec2(dot(p,vec2(12.71,31.17)), dot(p,vec2(26.95,18.33)));
    return fract(sin(q)*43758.5453);
}

float dropletnoise(in vec2 x)
{
    if (dropletIntensity < 0.001) return 0.;

    x *= dropletIntensity;

    vec2 p = floor(x);
    vec2 f = fract(x);


    float va = 0.0;
    for( int j=-1; j<=1; j++ )
    for( int i=-1; i<=1; i++ )
    {
        vec2 g = vec2(float(i), float(j));
        vec2 o = droplethash3(p + g);
        vec2 r = g - f + o;
        float d = length(r) / dropletIntensity;

        float a = max(cos(d - waterWaveCounter * 2.7 + (o.x + o.y) * 5.0), 0.);
        a = smoothstep(0.99, 0.999, a);

        float ripple = mix(a, 0., d);
        va += max(ripple, 0.);
    }

    return va;
}

void main()
{
	// When looking through tinted glass you can clearly see the edges where we fade to sky color
	// Using this discard seems to completely fix that
	if (rgba.a < 0.005) discard;
	float murkiness=max(0, getUnderwaterMurkiness() - fogAmount);
	if (murkiness > 0.05) discard;



	vec4 texColor;

	float vn = max(0, 0.9 - abs(fragNormal.y));
	float wfc;

	bool isLava = (waterFlags & LiquidIsLavaBitMask) > 0;
	bool fullAlpha = (waterFlags & LiquidFullAlphaBitMask) > 0;

	if (isLava) wfc = waterFlowCounter * 0.1 * (1 + 5 * vn);
	else wfc = waterFlowCounter * (1 + 5 * vn);

	float flowSpeed = length(flowVectorf);
	if (flowSpeed > 0.001) {
		vec2 flowVec = normalize(flowVectorf) * flowSpeed;

		if (fragNormal.y < 0) wfc*=-1;

		vec2 uvxOffset =
			clamp(
				mod((uv - uvBase) + flowVec * wfc * blockTextureSize, blockTextureSize),
				vec2(1 / textureAtlasSize),
				blockTextureSize - 1 / textureAtlasSize)
		;

		texColor = texture(optimumTextures2D[terrainTex], uvBase + uvxOffset);

	} else {
		// Needs to be rewritten to not do weird uv-inverse math but simply use a second texture so json blocks can use it too

		vec2 uvxOffset =
			clamp(
				blockTextureSize - uvSize,
				vec2(1 / textureAtlasSize),
				blockTextureSize - 1 / textureAtlasSize)
		;

		texColor = texture(optimumTextures2D[terrainTex], uv) * waterStillCounterOff + (1-waterStillCounterOff) * texture(optimumTextures2D[terrainTex], uvBase + uvxOffset);
	}

	texColor = getColorMapped(optimumTextures2D[terrainTex], texColor);

	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, fragWorldPos, 0);


	vec4 rgbaFinal = rgba;
	if (OPTIMUM_FOAMEFFECT > 0) {
	rgbaFinal.a = rgba.a + max(0, texColor.a - 0.4);
	} else {
	rgbaFinal.a = rgba.a + texColor.a;
	}
	float bright = (rgba.r + rgba.g + rgba.b)/3;

	float shadowBright = getBrightnessFromShadowMap();


	float x = gl_FragCoord.x / frameSize.x;
	float y = gl_FragCoord.y / frameSize.y;

	// This seems to fix being able to see rivers when looking up from inside a lake
	//if (fogAmount > 0.98) discard; - Breaks new murky water rendering

	if (fullAlpha) {
		rgbaFinal.a=1;
	}

	if (isLava) {
		texColor *= vec4(vec3((rgbaFinal.r + rgbaFinal.g + rgbaFinal.b)/3 * shadowBright), rgbaFinal.a);
	} else {
		texColor *= vec4(rgbaFinal.rgb * shadowBright, rgbaFinal.a);
	}

	if (flowSpeed > 0) {
		texColor.a *= 1.2 * flowSpeed;
	}

	bool doLightFoam = (waterFlags & LiquidWeakFoamBitMask) != 0;

	// Was * 2 but that made water behind quartz glass super visible in the night
	// Was * 0.5 but that made water columns hardly visible
	float accuWeight = 1;

	if (OPTIMUM_FOAMEFFECT > 0) {
	if (rgbaFinal.a > 0) {

		// Water edge + shinyness shading effect, kinda nice
		float ownDepth = linearDepth(gl_FragCoord.z);
		float diffTotal = 0;
		int range = 2;
		for (int dx = -range; dx <= range; dx++) {
			for (int dy = -range; dy <= range; dy++) {
				float diff = ownDepth - linearDepth(texture(optimumTextures2D[depthTex], vec2(x + dx/frameSize.x, y + dy/frameSize.y)).x);
				if (diff < 0.001) { // This check prevents foam not rendered when looking through grass at distant water
					diffTotal += abs(diff);
				}
			}
		}

		diffTotal /= (4*range * range);
		diffTotal = min(diffTotal, -vn/10 + 0.05);

		if (isLava) {
			float intensity = clamp(dot(fragNormal, vec3(0, 1, 0)), 0, 1) * 0.5;
			float a = fragWorldPos.x + fragWorldPos.y - 1.5 * flowVectorf.x * wfc;
			float b = fragWorldPos.z - 1.5 * flowVectorf.y * wfc;

			float diff = intensity * clamp(1 - diffTotal*1000 - gnoise(vec3(a*35, b*35, wfc))/2 + gnoise(vec3(a*2, b*2, wfc))/2, 0, 1);
			float noise = intensity * (gnoise(vec3(a, b, wfc)) + 0.5) / 2;
			float rgbAdd = bright*(diff * 0.3 + noise/10);
			texColor.rgb -= vec3(rgbAdd, rgbAdd, rgbAdd);
			texColor.a=1; // Let's just do lava as opaque as we can
			accuWeight=1;

			float blackSpots = gnoise(fragWorldPos.xyz) + 0.5;

			texColor.rgb -= blackSpots * 1.5 * 0.5;
			texColor.g += 0.2 * 0.5;

		} else {
			// Cold liquids
			vec3 localPos = fragWorldPos.xyz;

			// Foam

			float intensity = clamp(dot(fragNormal, vec3(0, 1, 0)), 0, 1);

			float a = localPos.x + localPos.y - 1.5 * flowVectorf.x * wfc;
			float b = localPos.z - 1.5 * flowVectorf.y * wfc;

			// without the -abs() one diagonal direction goes derpy noise
			float noise1 = gnoise(vec3(a*15, -abs(b*15), wfc)) + gnoise(vec3(a*5, b*5, wfc));
			float noise2 = gnoise(vec3(a, b, wfc));

			float diff = intensity * clamp(1 - diffTotal*1500 - noise1/2 + noise2/2, 0, 1);
			float noise = intensity * (gnoise(vec3(a * 0.4, b * 0.4, wfc))/2 + gnoise(vec3(a, b, wfc))/2 + 0.5) / 2;

			float rgbAdd = max(0, bright*(diff * 0.3 + noise/10));
			if (doLightFoam) {
				rgbAdd *= 0.5;
			}

			texColor.rgb += vec3(rgbAdd, rgbAdd, rgbAdd);
			texColor.a += (max(0, diff/16 + noise/(12 - 8*min(1,windSpeed))) + vn / 4) / clamp(fresnel, 0.5, 1);



			// Droplet noise
			float f = 0;
			if (skyExposed > 0) {
				vec2 uv = localPos.xz * (5 + noise1/20000.0);
				f = dropletnoise(uv);
			}



			// Specular reflection
			// GLSL 330: `#if SHADOWQUALITY == 0` opens `if (skyExposed > 0) {` here and closes it below, so
			// the block runs unconditionally with shadows on and only on sky-exposed liquid with them off.
			if (OPTIMUM_SHADOWQUALITY != 0 || skyExposed > 0) {
				vec3 noisepos = vec3(localPos.x , localPos.z, waterWaveCounter / 8 + windWaveCounter / 6);

				//float dy = clamp(noise2 / 10, 0, 1) + gnoise(noisepos); - trippy specular rings

				float dy = noise2 / 20 + clamp(gnoise(noisepos) / 10, 0, 0.6);

				vec3 normal = normalize(vec3(dy, 1, -dy));

				float upness = max(0, dot(fragNormal, vec3(0,1,0))); // Only do specular reflections on up faces

				vec3 eye = normalize(vec3(fWorldPos.x, fWorldPos.y - 2, fWorldPos.z));
				vec3 reflectionVec = reflect(sunPosRel, normal);
				float p = dot(reflectionVec, eye);
				if (p > 0) {
					float sunb = clamp(sunPosRel.y * 10, 0, 1) * clamp(1.5 - sunPosRel.y, 0, 1) * sunSpecularIntensity;

					float specular = pow(p, 50) * sunb;

					// Declared before the branch that assigns it (contract section 5); both paths overwrite the 0.
					float weight = 0.0;
					if (OPTIMUM_SHADOWQUALITY > 0) {
					weight = upness * clamp(specular * clamp(pow(shadowBright, 4), 0, 1) * clamp(1.5 * shadowIntensity, 0, 1), 0, 1);
					} else {
					weight = upness * clamp(specular * clamp(pow(shadowBright, 4), 0, 1) * clamp(1.5, 0.0, 1.0), 0, 1);
					}

					vec3 sunColf = applyFog(vec4(reflectColor, 1), fogAmount).rgb;

					texColor.rgb = mix(texColor.rgb, sunColf + noise1 * 0.2, weight);
					texColor.a = mix(texColor.a, texColor.a + specular/2, weight);
				}
			}

			texColor.rgb *= 1 + f;
			texColor.a += max(0, 0.5 - texColor.a)*0.5*f; // Some extra alpha for droplet noise where there is low alpha

		}
	}

	} else {
	if (isLava) {
		texColor.a=1;
		accuWeight=1;
	}
	}


	texColor = applyFog(texColor, fogAmount);
	texColor.a = clamp(texColor.a + fogAmount, 0, 1);

	texColor = applySpheresFog(texColor, fogAmount, fWorldPos.xyz);

#if USEOIT > 0
	OIT(texColor, glowLevel);
#endif

}

#endif
