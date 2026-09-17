#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of guigear.fsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "guigear.interface.glsl"

layout(location = 0) in vec2 uv;
layout(location = 1) in vec2 pos;

layout(location = 0) out vec4 outColor;

#include "noise3d.glsl"

void main () {
	outColor = texture(optimumTextures2D[tex2d], uv);
	if (outColor.a <= 0.01) discard;

	vec3 tealCol = vec3(56/255.0, 232/255.0, 182/255.0);
	vec3 tealDarkCol = vec3(80/255.0, 98/255.0, 93/255.0);

	float noise = abs(gnoise(vec3(pos.x/20, pos.y/20, gearCounter * 0.75)));
	float height = gearHeight - 10;

	float relY = (pos.y - hotbarYPos + height * 0.685) / height; // No idea why the 0.68 and not 0.55

	float ya = 1 - relY;
	float yb = 0.5 + stabilityLevel/2 + noise/30.0;

	if (ya < yb) {
		float b = 1.2f * (max(0.0, 0.4 - (outColor.r + outColor.g + outColor.b) / 3) + noise/5);

		outColor.rgb = mix(outColor.rgb, tealCol - b, clamp((yb - ya)*50, 0, 1));
	}

	outColor.rgb *= 1 - max(0.0, pos.y-shadeYPos)/15;
	outColor.a *= 1;//0.5;

	//outColor.rgb = vec3(relY);

}
