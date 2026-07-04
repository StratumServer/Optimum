#version 330 core

uniform sampler2D inputTexture;

in vec2 frameSize;
in vec2 texCoords[9];

out vec4 outColor;

// Optimum: 9-tap linear-sampled reduction of the vanilla 17-tap Gaussian
// kernel (see http://dev.theomader.com/gaussian-kernel-calculator/ for the
// source weights, and blur.vsh for the pairing/offset derivation). Weights
// below are the combined weight of each collapsed pair; they sum to the
// same 1.000001 the vanilla 17 constants summed to.
void main(void)
{
	vec4 out_colour = vec4(0.0);
	out_colour += texture(inputTexture, texCoords[0]) * 0.005677;
	out_colour += texture(inputTexture, texCoords[1]) * 0.035575;
	out_colour += texture(inputTexture, texCoords[2]) * 0.126531;
	out_colour += texture(inputTexture, texCoords[3]) * 0.255886;
	out_colour += texture(inputTexture, texCoords[4]) * 0.152663;
	out_colour += texture(inputTexture, texCoords[5]) * 0.255886;
	out_colour += texture(inputTexture, texCoords[6]) * 0.126531;
	out_colour += texture(inputTexture, texCoords[7]) * 0.035575;
	out_colour += texture(inputTexture, texCoords[8]) * 0.005677;

	outColor = out_colour;
}
