#version 330 core
// AMD FidelityFX Super Resolution 1 RCAS, adapted for a fragment pass.
// FidelityFX FSR 1 source carries the MIT license, AMD 2021.

uniform sampler2D inputScene;
uniform vec2 inputTexelSize;

in vec2 texCoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	vec3 b = texture(inputScene, texCoord + vec2(0.0, -inputTexelSize.y)).rgb;
	vec3 d = texture(inputScene, texCoord + vec2(-inputTexelSize.x, 0.0)).rgb;
	vec3 e = texture(inputScene, texCoord).rgb;
	vec3 f = texture(inputScene, texCoord + vec2(inputTexelSize.x, 0.0)).rgb;
	vec3 h = texture(inputScene, texCoord + vec2(0.0, inputTexelSize.y)).rgb;

	vec3 minimumRing = min(min(b, d), min(f, h));
	vec3 maximumRing = max(max(b, d), max(f, h));
	vec3 hitMinimum = min(minimumRing, e) / max(4.0 * maximumRing, vec3(1.0 / 65536.0));
	vec3 hitMaximumDenominator = min(4.0 * minimumRing - vec3(4.0), vec3(-1.0 / 65536.0));
	vec3 hitMaximum = (vec3(1.0) - max(maximumRing, e)) / hitMaximumDenominator;
	vec3 lobeChannels = max(-hitMinimum, hitMaximum);
	float lobe = max(-0.1875, min(max(max(lobeChannels.r, lobeChannels.g), lobeChannels.b), 0.0));
	lobe *= exp2(-0.2);

	vec3 sharpened = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);
	outColor = vec4(clamp(sharpened, 0.0, 1.0), 1.0);
}
