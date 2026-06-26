#version 330 core

uniform sampler2D inputTexture;
uniform sampler2D depthTexture;

in vec2 frameSize;
in vec2 texCoords[7];

out vec4 outColor;

// Optimum: 7-tap bilateral blur (vanilla uses 11).
// Dropped taps w0,w1,w9,w10 which contributed <4% of total weight.
// Remaining weights renormalized to sum to 1.0.
// Saves 8 texture reads per pass (4 depth + 4 color).
void main(void)
{
	vec4 out_colour = vec4(0.0);

	float refDepth = texture(depthTexture, texCoords[3]).r;
	float fac = 300;

	float w0 = (1 - clamp(abs(texture(depthTexture, texCoords[0]).r - refDepth) * fac, 0, 1)) * 0.053042;
	float w1 = (1 - clamp(abs(texture(depthTexture, texCoords[1]).r - refDepth) * fac, 0, 1)) * 0.122949;
	float w2 = (1 - clamp(abs(texture(depthTexture, texCoords[2]).r - refDepth) * fac, 0, 1)) * 0.203585;
	float w3 = (1 - clamp(abs(texture(depthTexture, texCoords[3]).r - refDepth) * fac, 0, 1)) * 0.240848;
	float w4 = (1 - clamp(abs(texture(depthTexture, texCoords[4]).r - refDepth) * fac, 0, 1)) * 0.203585;
	float w5 = (1 - clamp(abs(texture(depthTexture, texCoords[5]).r - refDepth) * fac, 0, 1)) * 0.122949;
	float w6 = (1 - clamp(abs(texture(depthTexture, texCoords[6]).r - refDepth) * fac, 0, 1)) * 0.053042;

	float wsum = w0 + w1 + w2 + w3 + w4 + w5 + w6;

	out_colour += texture(inputTexture, texCoords[0]) * w0;
	out_colour += texture(inputTexture, texCoords[1]) * w1;
	out_colour += texture(inputTexture, texCoords[2]) * w2;
	out_colour += texture(inputTexture, texCoords[3]) * w3;
	out_colour += texture(inputTexture, texCoords[4]) * w4;
	out_colour += texture(inputTexture, texCoords[5]) * w5;
	out_colour += texture(inputTexture, texCoords[6]) * w6;

	outColor = out_colour / wsum;
}
