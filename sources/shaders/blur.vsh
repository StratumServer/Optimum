#version 330 core

uniform vec2 frameSize;
uniform int isVertical;

out vec2 texCoords[9];

// Optimum: 9-tap linear-sampled reduction of vanilla's 17-tap Gaussian
// bloom blur (see blur.fsh). The blur framebuffers sample with GL_LINEAR
// (ClientPlatformWindows.cs::setupAttachment), so a single texture() fetch
// between two adjacent texels already computes a hardware-interpolated
// weighted average of the two. For adjacent integer offsets o1, o2=o1+1
// with kernel weights w1, w2, sampling at
//   offset = (o1*w1 + o2*w2) / (w1+w2)
// with combined weight w1+w2 reproduces w1*sample(o1) + w2*sample(o2)
// exactly: linear interpolation at fractional position t between texel o1
// and o2 computes color(o1)*(1-t) + color(o2)*t, and t = w2/(w1+w2) is the
// exact value that makes that equal the two-tap weighted sum. This is not
// an approximation of the pair, only of nothing -- the only source of
// error versus the vanilla 17 discrete taps is the GPU's own bilinear
// filtering precision.
//
// Vanilla 17-tap offsets/weights (blur.fsh, symmetric about 0):
//   0: 0.152663
//   ±1: 0.141908  ±2: 0.113978  ±3: 0.0791    ±4: 0.047431
//   ±5: 0.024574  ±6: 0.011001  ±7: 0.004255  ±8: 0.001422
// Paired (1,2) (3,4) (5,6) (7,8), mirrored to the negative side:
//   pair(1,2): w=0.255886 at offset 1.445425
//   pair(3,4): w=0.126531 at offset 3.374857
//   pair(5,6): w=0.035575 at offset 5.309234
//   pair(7,8): w=0.005677 at offset 7.250484
// 9 taps total (4 pairs each side + center), weights sum to 1.000001,
// matching the vanilla kernel's own rounding.
const float kOffsets[9] = float[9](-7.250484, -5.309234, -3.374857, -1.445425, 0.0, 1.445425, 3.374857, 5.309234, 7.250484);

void main(void)
{
	float x = -1.0 + float((gl_VertexID & 1) << 2);
    float y = -1.0 + float((gl_VertexID & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    vec2 texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

	if (isVertical == 1) {
		float pixelSize = 1.0 / frameSize.y;

		for (int i = 0; i < 9; i++) {
			texCoords[i] = texCoord + vec2(0, pixelSize * kOffsets[i]);
		}

	} else {
		float pixelSize = 1.0 / frameSize.x;

		for (int i = 0; i < 9; i++) {
			texCoords[i] = texCoord + vec2(pixelSize * kOffsets[i], 0);
		}
	}
}
