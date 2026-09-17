#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of colorgrade.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "colorgrade.interface.glsl"

layout(location = 1) in vec2 invFrameSize;
layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;

#include "fxaa.glsl"
#include "colorutil.glsl"

float SmoothStep(float x) { return x * x * (3.0f - 2.0f * x); }

vec4 ColorGrade(vec4 color) {
	// I don't know why, but this seems to make the scene look a lot better
	color.a = dot(color.rgb, vec3(0.299, 0.587, 0.114));

	vec3 hsl = rgb2hsl(color.rgb);

	float lightRange = maxlight - minlight;
	float satRange = maxsat - minsat;

	hsl.z = pow((clamp(hsl.z, minlight, maxlight) - minlight) / lightRange, 1/gammaLevel);
	hsl.y = pow((clamp(hsl.y, minsat, maxsat) - minsat) / satRange, 1);

	color.rgb = hsl2rgb(hsl);

	//c.rgb *= brightnessLevel;

	// Sepia
	vec3 sepia = vec3(
		(color.r * 0.393) + (color.g * 0.769) + (color.b * 0.189),
		(color.r * 0.349) + (color.g * 0.686) + (color.b * 0.168),
		(color.r * 0.272) + (color.g * 0.534) + (color.b * 0.131)
	);

	color.rgb = mix(color.rgb, sepia, sepiaLevel);

	// Vignetting
	vec2 position = (gl_FragCoord.xy * invFrameSize.xy) - vec2(0.5);
	float vignette = 1 - smoothstep(1.1- damageVignetting / 4, 0.75 - 0.45, length(position));
	color.rgb = mix(color.rgb, vec3(0.8 * damageVignetting/2, 0, 0), vignette);

	// Limit brightness
	//float b = (color.r + color.b + color.g) / 3;
	//color.rgb /= max(1, b);

	return color;
}


void main(void)
{
	vec4 color = texture(optimumTextures2D[primaryScene], texCoord);
	outColor = ColorGrade(color);
}
