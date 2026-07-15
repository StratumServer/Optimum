#version 330 core
// AMD FidelityFX Super Resolution 1 EASU, adapted for the Vintage Story final blit.
// FidelityFX FSR 1 source carries the MIT license, AMD 2021.

uniform sampler2D inputScene;
uniform vec2 inputTexelSize;

in vec2 texCoord;

layout(location = 0) out vec4 outColor;

float FsrRcp(float value)
{
	return 1.0 / max(value, 1.0 / 65536.0);
}

vec3 FsrSample(vec2 pixelCenter)
{
	vec2 halfTexel = inputTexelSize * 0.5;
	vec2 uv = clamp(pixelCenter * inputTexelSize, halfTexel, vec2(1.0) - halfTexel);
	return texture(inputScene, uv).rgb;
}

void FsrEasuTap(
	inout vec3 accumulatedColor,
	inout float accumulatedWeight,
	vec2 offset,
	vec2 direction,
	vec2 length,
	float lobe,
	float clippingPoint,
	vec3 color)
{
	vec2 distance;
	distance.x = offset.x * direction.x + offset.y * direction.y;
	distance.y = offset.x * -direction.y + offset.y * direction.x;
	distance *= length;
	float distanceSquared = min(dot(distance, distance), clippingPoint);
	float baseWindow = (2.0 / 5.0) * distanceSquared - 1.0;
	float lobeWindow = lobe * distanceSquared - 1.0;
	baseWindow *= baseWindow;
	lobeWindow *= lobeWindow;
	baseWindow = (25.0 / 16.0) * baseWindow - (25.0 / 16.0 - 1.0);
	float weight = baseWindow * lobeWindow;
	accumulatedColor += color * weight;
	accumulatedWeight += weight;
}

void FsrEasuSet(
	inout vec2 direction,
	inout float length,
	vec2 fractionalPosition,
	bool quadrantS,
	bool quadrantT,
	bool quadrantU,
	bool quadrantV,
	float lumaA,
	float lumaB,
	float lumaC,
	float lumaD,
	float lumaE)
{
	float weight = 0.0;
	if (quadrantS) weight = (1.0 - fractionalPosition.x) * (1.0 - fractionalPosition.y);
	if (quadrantT) weight = fractionalPosition.x * (1.0 - fractionalPosition.y);
	if (quadrantU) weight = (1.0 - fractionalPosition.x) * fractionalPosition.y;
	if (quadrantV) weight = fractionalPosition.x * fractionalPosition.y;

	float lengthX = FsrRcp(max(abs(lumaD - lumaC), abs(lumaC - lumaB)));
	float directionX = lumaD - lumaB;
	lengthX = clamp(abs(directionX) * lengthX, 0.0, 1.0);
	lengthX *= lengthX;

	float lengthY = FsrRcp(max(abs(lumaE - lumaC), abs(lumaC - lumaA)));
	float directionY = lumaE - lumaA;
	lengthY = clamp(abs(directionY) * lengthY, 0.0, 1.0);
	lengthY *= lengthY;

	direction += vec2(directionX, directionY) * weight;
	length += (lengthX + lengthY) * weight;
}

float FsrLuma(vec3 color)
{
	return color.g + 0.5 * (color.r + color.b);
}

vec3 FsrEasu(vec2 outputUv)
{
	vec2 inputSize = 1.0 / inputTexelSize;
	vec2 position = outputUv * inputSize - vec2(0.5);
	vec2 base = floor(position);
	vec2 fractionalPosition = position - base;

	vec3 b = FsrSample(base + vec2(0.5, -0.5));
	vec3 c = FsrSample(base + vec2(1.5, -0.5));
	vec3 e = FsrSample(base + vec2(-0.5, 0.5));
	vec3 f = FsrSample(base + vec2(0.5, 0.5));
	vec3 g = FsrSample(base + vec2(1.5, 0.5));
	vec3 h = FsrSample(base + vec2(2.5, 0.5));
	vec3 i = FsrSample(base + vec2(-0.5, 1.5));
	vec3 j = FsrSample(base + vec2(0.5, 1.5));
	vec3 k = FsrSample(base + vec2(1.5, 1.5));
	vec3 l = FsrSample(base + vec2(2.5, 1.5));
	vec3 n = FsrSample(base + vec2(0.5, 2.5));
	vec3 o = FsrSample(base + vec2(1.5, 2.5));

	float bL = FsrLuma(b);
	float cL = FsrLuma(c);
	float eL = FsrLuma(e);
	float fL = FsrLuma(f);
	float gL = FsrLuma(g);
	float hL = FsrLuma(h);
	float iL = FsrLuma(i);
	float jL = FsrLuma(j);
	float kL = FsrLuma(k);
	float lL = FsrLuma(l);
	float nL = FsrLuma(n);
	float oL = FsrLuma(o);

	vec2 direction = vec2(0.0);
	float length = 0.0;
	FsrEasuSet(direction, length, fractionalPosition, true, false, false, false, bL, eL, fL, gL, jL);
	FsrEasuSet(direction, length, fractionalPosition, false, true, false, false, cL, fL, gL, hL, kL);
	FsrEasuSet(direction, length, fractionalPosition, false, false, true, false, fL, iL, jL, kL, nL);
	FsrEasuSet(direction, length, fractionalPosition, false, false, false, true, gL, jL, kL, lL, oL);

	float directionLengthSquared = dot(direction, direction);
	bool zeroDirection = directionLengthSquared < (1.0 / 32768.0);
	if (zeroDirection)
	{
		direction = vec2(1.0, 0.0);
	}
	else
	{
		direction *= inversesqrt(directionLengthSquared);
	}

	length *= 0.5;
	length *= length;
	float stretch = FsrRcp(max(abs(direction.x), abs(direction.y)));
	vec2 anisotropicLength = vec2(1.0 + (stretch - 1.0) * length, 1.0 - 0.5 * length);
	float lobe = 0.5 + ((0.25 - 0.04) - 0.5) * length;
	float clippingPoint = FsrRcp(lobe);

	vec3 accumulatedColor = vec3(0.0);
	float accumulatedWeight = 0.0;
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(0.0, -1.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, b);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(1.0, -1.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, c);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(-1.0, 1.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, i);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(0.0, 1.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, j);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(0.0, 0.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, f);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(-1.0, 0.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, e);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(1.0, 1.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, k);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(2.0, 1.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, l);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(2.0, 0.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, h);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(1.0, 0.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, g);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(1.0, 2.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, o);
	FsrEasuTap(accumulatedColor, accumulatedWeight, vec2(0.0, 2.0) - fractionalPosition, direction, anisotropicLength, lobe, clippingPoint, n);

	vec3 result = accumulatedColor / accumulatedWeight;
	vec3 minimumColor = min(min(f, g), min(j, k));
	vec3 maximumColor = max(max(f, g), max(j, k));
	return clamp(result, minimumColor, maximumColor);
}

void main(void)
{
	outColor = vec4(clamp(FsrEasu(texCoord), 0.0, 1.0), 1.0);
}
