#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of godrays.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of godrays.fsh (the Optimum override in sources/shaders, docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of godrays (docs/vulkan.md). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots and every other uniform is a record member:
// godrays.vsh's seven, then godrays.fsh's one, each in declaration order.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, inputTexture);
    OPTIMUM_SAMPLER_SLOT(sampler2D, glowParts);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec2 invFrameSizeIn;
    vec3 sunPosScreenIn;
    vec3 sunPos3dIn;
    vec3 playerViewVector;
    float iGlobalTimeIn;
    float directionIn;
    int dusk;

    int maxGodRaySamples;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) out vec2 texCoord;
layout(location = 1) out vec3 sunPosScreen;
layout(location = 2) out float iGlobalTime;
layout(location = 3) out float intensity;
layout(location = 4) out float direction;

void main(void)
{
	// https://randallr.wordpress.com/2014/06/14/rendering-a-screen-covering-triangle-in-opengl
	float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);

	sunPosScreen = sunPosScreenIn;
	iGlobalTime = iGlobalTimeIn;

	float sunPlrAngle = (dot(sunPos3dIn, playerViewVector) + 1) / 3;

	direction = dot(sunPos3dIn, playerViewVector) >= 0 ? 1 : -1;

	// https://www.toolfk.com/online-plotter-frame/#W3sidHlwZSI6MCwiZXEiOiJtYXgoMSwxLjc1KigxLTYqYWJzKHgtMC4yMikpKSIsImNvbG9yIjoiIzAwMDAwMCJ9LHsidHlwZSI6MTAwMCwid2luZG93IjpbIi0xIiwiMSIsIjAiLCIyIl19XQ--
	float dawnMul = max(1, (1-dusk) * 2 * (1 - 6*abs(sunPos3dIn.y - 0.1)));

	// Intensity is determined by how directly the player is looking at the sun
	// above intensity 0.8 we get godrays where they shouldn't be o.o
	intensity = clamp(sunPlrAngle * dawnMul, 0, 0.8);

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)


layout(location = 0) in vec2 texCoord;
layout(location = 1) in vec3 sunPosScreen;
layout(location = 2) in float iGlobalTime;
layout(location = 3) in float intensity;
layout(location = 4) in float direction;

layout(location = 0) out vec4 outColor;


// Falloff over distance
const float decay = 0.9985;

float hash(vec2 p) { return fract(sin(dot(p, vec2(41, 289)))*45758.5453); }


vec2 clampDeltas(vec2 dtuv) {
	// When looking 90 degrees away from the sun, dTuv gets very large and causes significant frame drops.
	// I presume this is because the graphics card local texture cache is no longer effective due to the large uv coord jumps
	if (length(dtuv) > 0.005) {
		dtuv = normalize(dtuv) * 0.005;
	}

	return dtuv;
}

vec4 applyGodRays(in vec2 uv, in vec2 nSunPos) {
	// Sample weight. Decays as we radiate outwards.
	float weight = intensity / 23.0 / 1.5;

	int vanillaSamples = int(180 * min(1, intensity * 1.2));
	int samples = min(maxGodRaySamples, vanillaSamples);

	// Short deltas near the sun
	vec2 sdTuv = clampDeltas((nSunPos - uv) * intensity / 200 * direction);

	// Large deltas far away from the sun where precision matters less and where is more important that the ray travels as far as possible
	vec2 ldTuv = clampDeltas((nSunPos - uv) * intensity / 64 * direction);

	vec2 dTuv = sdTuv;


	float glow = texture(optimumTextures2D[glowParts], uv).g;
    vec4 col = texture(optimumTextures2D[inputTexture], uv) * glow;

    for (float i=0.0; i < samples; i++) {
		uv.x = clamp(uv.x + dTuv.x, 0, 1);
		uv.y = clamp(uv.y + dTuv.y, 0, 1);
        col += texture(optimumTextures2D[inputTexture], uv) * texture(optimumTextures2D[glowParts], uv).g * weight;
        weight *= decay;

		dTuv = mix(sdTuv, ldTuv, i/samples);
    }

	// Seems to greatly reduce the sun turning into one massive white blob
	col.rgb *= clamp(1 - max((col.r+col.g+col.b)/3 - 0.7, 0), 0, 1);

	col.a = min(1, col.a);

    return col;
}


void main(void) {
	vec2 nSunPos = (clamp(sunPosScreen.xy, -10, 10) + 1) / 2;
	outColor = applyGodRays(texCoord, nSunPos);

	outColor.a=1;
}

#endif
