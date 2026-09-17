#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of gui.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "gui.interface.glsl"
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
