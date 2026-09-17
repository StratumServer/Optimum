// Native port of the game include colormap.fsh (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: colormap.fsh
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_COLORMAP_FRAG_GLSL
#define OPTIMUM_INCLUDE_COLORMAP_FRAG_GLSL

#include "varyings.glsl"

layout(location = OPTIMUM_LOCATION_CLIMATE_COLOR_MAP_UV) in vec2 climateColorMapUv;
layout(location = OPTIMUM_LOCATION_SEASON_COLOR_MAP_UV) in vec2 seasonColorMapUv;
layout(location = OPTIMUM_LOCATION_FROST_ALPHA) in float frostAlpha;
layout(location = OPTIMUM_LOCATION_SEASON_WEIGHT) in float seasonWeight;
layout(location = OPTIMUM_LOCATION_HERETEMP) in float heretemp;

vec4 getColorMapped(sampler2D sourceTex, vec4 color) {
	vec4 tint = vec4(1);
	bool mapped = false;

	if (climateColorMapUv.x >= 0) {
		tint = texture(sourceTex, climateColorMapUv);
		mapped=true;
	}

	if (seasonColorMapUv.x >= 0 && seasonWeight > 0) {
		vec4 seasonColor = texture(sourceTex, seasonColorMapUv);
		tint = mix(tint, seasonColor, seasonWeight);
		mapped=true;
	}

	if (frostAlpha > 0) {
		float w = clamp((0.333 - heretemp) * 15, 0, 1);

		if (mapped) {
			tint.rgb = mix(tint.rgb, tint.rgb * (1 - frostAlpha) + vec3(1) * frostAlpha, w);
		} else {
			float b = (color.r + color.g + color.b) / 3.0;

			vec3 frostColor = vec3(b + frostAlpha*0.2);
			float faw = frostAlpha * w;
			color.rgb = color.rgb * (1 - faw) + frostColor * faw;
			return color;
		}
	}

	return color * tint;
}

vec4 getFrosted(vec4 color) {
	if (heretemp < 0.333 && frostAlpha > 0) {
		float w = clamp((0.333 - heretemp) * 15, 0, 1);

		float b = (color.r + color.g + color.b) / 3.0;

		vec3 frostColor = vec3(b + frostAlpha*0.2);
		float faw = frostAlpha * w;
		color.rgb = color.rgb * (1 - faw) + frostColor * faw;
	}
	return color;
}

#endif
