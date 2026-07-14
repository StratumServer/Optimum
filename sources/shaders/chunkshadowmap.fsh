#version 330 core

uniform sampler2D tex2d;

in vec2 uv;
out vec4 outColor;

void main () {
	outColor = texture(tex2d, uv);
	// Optimum: raise discard threshold from 0.02 to 0.15.
	// Skips more near-transparent shadow fragments (grass edges, leaf fringes)
	// with no visible shadow quality loss at typical view distances.
	if (outColor.a < 0.15) discard;

}
