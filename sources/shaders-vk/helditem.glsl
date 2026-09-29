#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of helditem.vsh (docs/vulkan.md).
// SSAOLEVEL > 0 gates the G-buffer varyings, so it is the GBUFFER axis (section 5).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of helditem.fsh (docs/vulkan.md).
// SSAOLEVEL > 0 gates the G-buffer inputs and outputs, so it is the GBUFFER axis; BLOOM and NORMALVIEW are
// specialization-constant branches with the same expressions. n is declared by the GLSL 330 stage but written
// by no vertex stage and read by nothing; the optimised module drops it.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of helditem (docs/vulkan.md). At most a couple of draws per Use(),
// so the push block holds only the sampler slots, in helditem.fsh's declaration order. The record holds
// helditem.vsh's uniforms, then helditem.fsh's, then normalshading.fsh's lightPosition (its owner
// fogandlight.fsh is not included), each in declaration order.
// The GLSL 330 initializers (alphaTest = 0.001, damageEffect = 0) are seeded by the runtime (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, itemTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2dOverlay);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaGlowIn;
    int extraGlow;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float alphaTest;
    float overlayOpacity;
    vec2 overlayTextureSize;
    vec2 baseTextureSize;
    vec2 baseUvOrigin;
    int normalShaded;
    float damageEffect;

    vec3 lightPosition;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 modelColor;
layout(location = 3) in int flags;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;

layout(location = 2) out vec3 normal;
layout(location = 3) out vec3 vertexPosition;
#if GBUFFER == 1
layout(location = 4) out vec4 fragPosition;
layout(location = 5) out vec4 gnormal;
#endif



#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	vec4 cameraPos = modelViewMatrix * vec4(vertexPositionIn, 1.0);

	int glow = min(255, extraGlow + (flags & GlowLevelBitMask));
	glowLevel = glow / 255.0;

	uv = uvIn;
	color = applyLight(
		rgbaAmbientIn,
		rgbaLightIn,
		glow,
		cameraPos
	) * modelColor;

	color.rgb = mix(color.rgb, rgbaGlowIn.rgb, glow / 255.0 * rgbaGlowIn.a);

	gl_Position = projectionMatrix * cameraPos;

	normal = unpackNormal(flags);
	normal = normalize((modelViewMatrix * vec4(normal.x, normal.y, normal.z, 0)).xyz);

	#if GBUFFER == 1
		fragPosition = cameraPos;
		gnormal = vec4(normal, 0);
	#endif

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if GBUFFER == 1
layout(location = 4) in vec4 fragPosition;
layout(location = 5) in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


// Texture overlay "hack"
// We only have the base texture UV coordinates, which, for blocks and items in inventory is the block or item texture atlas, but none uv coords for a dedicated overlay texture
// So lets remove the base offset (baseUvOrigin) and rescale the coords (baseTextureSize / overlayTextureSize) to get useful UV coordinates for the overlay texture

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 6) in float n;
layout(location = 2) in vec3 normal;
layout(location = 3) in vec3 vertexPosition;

#include "vertexflagbits.glsl"
#include "normalshading.glsl"
#include "noise2d.glsl"

void main () {
	float b = 1;

	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	if (overlayOpacity > 0) {
		vec2 uvOverlay = (uv - baseUvOrigin) * (baseTextureSize / overlayTextureSize);

		vec4 col1 = texture(optimumTextures2D[tex2dOverlay], uvOverlay);
		vec4 col2 = texture(optimumTextures2D[itemTex], uv);

		float a1 = overlayOpacity * col1.a  * min(1, col2.a * 100);
		float a2 = col2.a * (1 - a1);

		outColor = vec4(
			(a1 * col1.r + col2.r * a2) / (a1+a2),
			(a1 * col1.b + col2.g * a2) / (a1+a2),
			(a1 * col1.g + col2.b * a2) / (a1+a2),
			a1 + a2
		) * color;

	} else {
		outColor = texture(optimumTextures2D[itemTex], uv) * color;
	}

	outColor.a = clamp(outColor.a, 0, 1); // No idea why, makes held torches glitchy without

	if (OPTIMUM_BLOOM == 0) {
		outColor.rgb *= 1 + glowLevel;
	}

	// Ensure held item always being in the front
	gl_FragDepth = gl_FragCoord.z / 20;

	if (outColor.a < alphaTest) discard;

	if (normalShaded > 0) {
		float b = min(1, getBrightnessFromNormal(normal, 1, 0.45) + glowLevel);
		outColor *= vec4(b, b, b, 1);
	}

#if GBUFFER == 1
	// Doesn't work properly for some reason
	//outGPosition = vec4(fragPosition.xyz, glowLevel);
	outGPosition = vec4(1);
	outGNormal = gnormal;
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
		outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

	outColor.rgb *= b;

	outGlow = vec4(glowLevel, 0, 0, outColor.a);
}

#endif
