#version 330 core

uniform sampler2D inputTexture;
uniform sampler2D glowParts;


in vec2 texCoord;
in vec3 sunPosScreen;
in float iGlobalTime;
in float intensity;
in float direction;

out vec4 outColor;


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
	
	// Optimum: cap max samples by quality level. GODRAYS=1 halves the work.
#if GODRAYS == 1
	int samples = int(90 * min(1, intensity * 1.2));
#else
	int samples = int(180 * min(1, intensity * 1.2));
#endif
	
	// Short deltas near the sun
	vec2 sdTuv = clampDeltas((nSunPos - uv) * intensity / 200 * direction);
	
	// Large deltas far away from the sun where precision matters less and where is more important that the ray travels as far as possible
	vec2 ldTuv = clampDeltas((nSunPos - uv) * intensity / 64 * direction);
	
	vec2 dTuv = sdTuv;
	
	
	float glow = texture(glowParts, uv).g;
    vec4 col = texture(inputTexture, uv) * glow;
    
    for (float i=0.0; i < samples; i++) {
		uv.x = clamp(uv.x + dTuv.x, 0, 1);
		uv.y = clamp(uv.y + dTuv.y, 0, 1);
        col += texture(inputTexture, uv) * texture(glowParts, uv).g * weight;
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
