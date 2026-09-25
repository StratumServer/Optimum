#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of gui.vsh (docs/vulkan.md).
// The Animation UBO is the set 2 animation storage buffer. MAXANIMATEDELEMENTS is a client setting the
// offline build cannot know, so the array is unsized; jointId indexes it exactly as before.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of gui.fsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of gui (docs/vulkan.md, GUI row). The push block holds the two
// sampler slots in gui.fsh's declaration order, then the per-element uniforms RenderAPIGame.RenderRectangle
// sets on every rectangle (rgbaIn, extraGlow, applyColor, noTexture, overlayOpacity). The record holds
// projectionMatrix, modelViewMatrix, modelMatrix and every other uniform: gui.vsh's in declaration order,
// then gui.fsh's, then normalshading.fsh's lightPosition (its owner fogandlight.fsh is not included).
//
// The GLSL 330 initializers (sepiaLevel = 0, damageEffect = 0) are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2d);
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2dOverlay);
    vec4 rgbaIn;
    int extraGlow;
    int applyColor;
    float noTexture;
    float overlayOpacity;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaGlowIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    mat4 modelMatrix;
    int applyModelMat;
    int applyAnimation;

    float alphaTest;
    int darkEdges;
    int tempGlowMode;
    int transparentCenter;
    int normalShaded;
    float sepiaLevel;
    float damageEffect;
    vec2 overlayTextureSize;
    vec2 baseTextureSize;
    vec2 baseUvOrigin;

    vec3 lightPosition;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
// Bits 0-7: Glow level
// Bits 8-10: Z-Offset
// Bit 11: Wind waving yes/no
// Bit 12: Water waving yes/no
// Bit 13: low contrast mode
// Bit 14-26: x/y/z normals, 12 bits total. Each axis with 1 sign bit and 3 value bits
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

layout(std140, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION) readonly buffer Animation
{
    mat4 values[];
} ElementTransforms;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 uvOverlay;
layout(location = 2) out vec4 color;
layout(location = 3) out vec4 rgbaGlow;
layout(location = 4) out vec2 clipPos;
layout(location = 5) out float damageEffectV;

layout(location = 6) flat out vec3 normal;
layout(location = 7) out float normalShadeIntensity;

#include "vertexflagbits.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	damageEffectV = damageEffectIn;
	uv = uvIn;

	int glow = min(255, extraGlow + (renderFlagsIn & GlowLevelBitMask));

	glowLevel = glow / 255.0;
	rgbaGlow = rgbaGlowIn;

	color = rgbaIn;

	if (applyColor == 1) color *= colorIn;

	if (applyAnimation > 0) {
		mat4 animModelMat = modelViewMatrix * ElementTransforms.values[jointId];
		gl_Position = projectionMatrix * animModelMat * vec4(vertexPositionIn, 1.0);
	} else {
		gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);
	}

	clipPos = gl_Position.xy;

	normal = unpackNormal(renderFlagsIn);
	if (applyModelMat > 0) {
		normal = (modelMatrix * vec4(normal, 0)).xyz;
		normal = normalize(normal);
	}

	normalShadeIntensity = 1;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) in vec2 uv;
layout(location = 2) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in vec2 clipPos;


layout(location = 0) out vec4 outColor;
layout(location = 3) in vec4 rgbaGlow;
layout(location = 5) in float damageEffectV;

// Texture overlay "hack"
// We only have the base texture UV coordinates, which, for blocks and items in inventory is the block or item texture atlas, but none uv coords for a dedicated overlay texture
// So lets remove the base offset (baseUvOrigin) and rescale the coords (baseTextureSize / overlayTextureSize) to get useful UV coordinates for the overlay texture


layout(location = 7) in float normalShadeIntensity;
layout(location = 6) flat in vec3 normal;


#include "vertexflagbits.glsl"
#include "normalshading.glsl"
#include "noise2d.glsl"

void main () {
	float b = 1;

	float def = damageEffectV + damageEffect;
	if (def > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < def - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-def));
	}

	if (darkEdges > 0) {
		float dx = 1.7 * abs(uv.x - 0.5) - 0.7;
		float dy = 1.7 * abs(uv.y - 0.5) - 0.7;
		float strength = clamp(max(dx,dy) * 0.85 + (1- (dx*dx + dy*dy)) * 0.15, 0, 0.5);

		outColor = vec4(0,0,0, strength);
		return;
	}

	if (noTexture > 0) {
		outColor = color;
	} else {
		if (overlayOpacity > 0) {
			vec2 uvOverlay = (uv - baseUvOrigin) * (baseTextureSize / overlayTextureSize);

			vec4 col1 = texture(optimumTextures2D[tex2dOverlay], uvOverlay);
			vec4 col2 = texture(optimumTextures2D[tex2d], uv);

			float a1 = overlayOpacity * col1.a  * min(1, col2.a * 100);
			float a2 = col2.a * (1 - a1);

			outColor = vec4(
				(a1 * col1.r + col2.r * a2) / (a1+a2),
				(a1 * col1.b + col2.g * a2) / (a1+a2),
				(a1 * col1.g + col2.b * a2) / (a1+a2),
				a1 + a2
			) * color;


		} else {
			outColor = texture(optimumTextures2D[tex2d], uv) * color;
		}
	}


	if (tempGlowMode == 1) {
		outColor.rgb += rgbaGlow.rgb * min(0.8, glowLevel + rgbaGlow.a);
	} else {
		outColor.rgb *= 1 + glowLevel;
	}

	if (transparentCenter > 0) {
		outColor.a *= clamp(pow(length(clipPos), 2) * 15, 0, 1);
	}

	if (outColor.a <= alphaTest) discard;

	if (normalShaded > 0) {
		float b = getBrightnessFromNormal(normal, normalShadeIntensity, 0.45) * 1.2;
		outColor *= vec4(b, b, b, 1);
	}

	if (sepiaLevel > 0) {
		// Sepia
		vec3 sepia = vec3(
			(outColor.r * 0.393) + (outColor.g * 0.769) + (outColor.b * 0.189),
			(outColor.r * 0.349) + (outColor.g * 0.686) + (outColor.b * 0.168),
			(outColor.r * 0.272) + (outColor.g * 0.534) + (outColor.b * 0.131)
		);

		outColor.rgb = mix(outColor.rgb, sepia * 1.33, sepiaLevel);
	}

	outColor.rgb *= b;

	//outColor.a=0.1;
}

#endif
