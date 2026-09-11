#version 330 core
// Optimum TAA (P5): post-resolve sharpening.
//
// AMD FidelityFX Super Resolution 1 RCAS, adapted for a fragment pass exactly
// as fsr-rcas.fsh is, with two differences that the TAA placement requires.
// FidelityFX FSR 1 source carries the MIT license, AMD 2021.
//
//  1. The lobe strength is a uniform instead of the baked exp2(-0.2) constant,
//     so OptimumConfig.TaaSharpness drives it. sharpness <= 0 is a TRUE bypass:
//     the centre texel is returned untouched, bit for bit, without going
//     through the filter or any clamp. That is what makes "TAA sharpen off"
//     indistinguishable from not running the pass at all.
//  2. This pass runs on the resolved HDR colour (RGBA16F), not on the LDR
//     image RCAS normally finishes. Clamping the result to [0,1] the way
//     fsr-rcas.fsh does would crush every highlight above 1, so only the
//     lower bound is kept. RCAS's own lobe term already guards the upper end:
//     above 1 its hitMaximum turns positive, the lobe clamps to 0 and such a
//     pixel is passed through unsharpened rather than being driven anywhere.

uniform sampler2D inputScene;
uniform vec2 inputTexelSize;
// 0 = bypass (see above), 1 = full RCAS strength. Mapped onto RCAS's own
// attenuation in stops: sharpness 1 is 0 stops of attenuation, and the linear
// factor in front takes the lobe continuously to zero as sharpness does, so
// there is no step between "almost off" and the bypass.
uniform float sharpness;

in vec2 texCoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	vec4 center = texture(inputScene, texCoord);
	if (!(sharpness > 0.0))
	{
		outColor = center;
		return;
	}

	vec3 b = texture(inputScene, texCoord + vec2(0.0, -inputTexelSize.y)).rgb;
	vec3 d = texture(inputScene, texCoord + vec2(-inputTexelSize.x, 0.0)).rgb;
	vec3 e = center.rgb;
	vec3 f = texture(inputScene, texCoord + vec2(inputTexelSize.x, 0.0)).rgb;
	vec3 h = texture(inputScene, texCoord + vec2(0.0, inputTexelSize.y)).rgb;

	vec3 minimumRing = min(min(b, d), min(f, h));
	vec3 maximumRing = max(max(b, d), max(f, h));
	vec3 hitMinimum = min(minimumRing, e) / max(4.0 * maximumRing, vec3(1.0 / 65536.0));
	vec3 hitMaximumDenominator = min(4.0 * minimumRing - vec3(4.0), vec3(-1.0 / 65536.0));
	vec3 hitMaximum = (vec3(1.0) - max(maximumRing, e)) / hitMaximumDenominator;
	vec3 lobeChannels = max(-hitMinimum, hitMaximum);
	float lobe = max(-0.1875, min(max(max(lobeChannels.r, lobeChannels.g), lobeChannels.b), 0.0));
	float strength = clamp(sharpness, 0.0, 1.0);
	lobe *= strength * exp2(-2.0 * (1.0 - strength));

	vec3 sharpened = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);
	outColor = vec4(max(sharpened, vec3(0.0)), center.a);
}
