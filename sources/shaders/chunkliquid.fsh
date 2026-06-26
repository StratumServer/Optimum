#version 330 core

uniform sampler2D terrainTex;
uniform sampler2D depthTex;

uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float waterFlowCounter;
uniform vec3 sunPosRel;
uniform vec3 sunColor;
uniform vec3 reflectColor;
uniform float waterWaveCounter;
uniform float sunSpecularIntensity;
uniform float dropletIntensity = 0;
uniform float windSpeed;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in vec2 uvSize;
in float waterStillCounterOff;
flat in vec2 uvBase;
in vec3 fragWorldPos;
in vec3 fWorldPos;
in vec3 fragNormal;
in float fresnel;
flat in int skyExposed;

in vec2 flowVectorf;
in float glowLevel;

flat in int waterFlags;
flat in int renderFoam;

#include fogandlight.fsh
#include noise3d.ash
#include colormap.fsh
#include underwatereffects.fsh
#include oit.fsh


vec2 droplethash3( vec2 p )
{
    //vec2 q = vec2(dot(p,vec2(127.1,311.7)), dot(p,vec2(269.5,183.3))); - causes too high values and weird distortions
	
	vec2 q = vec2(dot(p,vec2(12.71,31.17)), dot(p,vec2(26.95,18.33)));
    return fract(sin(q)*43758.5453);
}

float dropletnoise(in vec2 x)
{
    if (dropletIntensity < 0.001) return 0.;
	
    x *= dropletIntensity;
    
    vec2 p = floor(x);
    vec2 f = fract(x);


    float va = 0.0;
    for( int j=-1; j<=1; j++ )
    for( int i=-1; i<=1; i++ )
    {
        vec2 g = vec2(float(i), float(j));
        vec2 o = droplethash3(p + g);
        vec2 r = g - f + o;
        float d = length(r) / dropletIntensity;
        
        float a = max(cos(d - waterWaveCounter * 2.7 + (o.x + o.y) * 5.0), 0.);
        a = smoothstep(0.99, 0.999, a);
        
        float ripple = mix(a, 0., d);
        va += max(ripple, 0.);
    }
	
    return va;
}

void main() 
{
	// When looking through tinted glass you can clearly see the edges where we fade to sky color
	// Using this discard seems to completely fix that
	if (rgba.a < 0.005) discard;
	float murkiness=max(0, getUnderwaterMurkiness() - fogAmount);
	if (murkiness > 0.05) discard;



	vec4 texColor;
	
	float vn = max(0, 0.9 - abs(fragNormal.y));
	float wfc;
	
	bool isLava = (waterFlags & LiquidIsLavaBitMask) > 0;
	bool fullAlpha = (waterFlags & LiquidFullAlphaBitMask) > 0;
	
	if (isLava) wfc = waterFlowCounter * 0.1 * (1 + 5 * vn);
	else wfc = waterFlowCounter * (1 + 5 * vn);

	float flowSpeed = length(flowVectorf);
	if (flowSpeed > 0.001) {
		vec2 flowVec = normalize(flowVectorf) * flowSpeed;
		
		if (fragNormal.y < 0) wfc*=-1;
		
		vec2 uvxOffset = 
			clamp(
				mod((uv - uvBase) + flowVec * wfc * blockTextureSize, blockTextureSize),
				vec2(1 / textureAtlasSize), 
				blockTextureSize - 1 / textureAtlasSize)
		;
		
		texColor = texture(terrainTex, uvBase + uvxOffset);
		
	} else {
		// Needs to be rewritten to not do weird uv-inverse math but simply use a second texture so json blocks can use it too
		
		vec2 uvxOffset = 
			clamp(
				blockTextureSize - uvSize,
				vec2(1 / textureAtlasSize), 
				blockTextureSize - 1 / textureAtlasSize)
		;
		
		texColor = texture(terrainTex, uv) * waterStillCounterOff + (1-waterStillCounterOff) * texture(terrainTex, uvBase + uvxOffset);				
	}
	
	texColor = getColorMapped(terrainTex, texColor);

	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, fragWorldPos, 0);


	vec4 rgbaFinal = rgba;
#if FOAMEFFECT > 0
	rgbaFinal.a = rgba.a + max(0, texColor.a - 0.4);
#else
	rgbaFinal.a = rgba.a + texColor.a;
#endif
	float bright = (rgba.r + rgba.g + rgba.b)/3;
	
	float shadowBright = getBrightnessFromShadowMap();
	
	
	float x = gl_FragCoord.x / frameSize.x;
	float y = gl_FragCoord.y / frameSize.y;
	
	// This seems to fix being able to see rivers when looking up from inside a lake
	//if (fogAmount > 0.98) discard; - Breaks new murky water rendering

	if (fullAlpha) {
		rgbaFinal.a=1;
	}

	if (isLava) {
		texColor *= vec4(vec3((rgbaFinal.r + rgbaFinal.g + rgbaFinal.b)/3 * shadowBright), rgbaFinal.a);
	} else {
		texColor *= vec4(rgbaFinal.rgb * shadowBright, rgbaFinal.a);
	}
	
	if (flowSpeed > 0) {
		texColor.a *= 1.2 * flowSpeed;
	}

	bool doLightFoam = (waterFlags & LiquidWeakFoamBitMask) != 0;
	
	// Was * 2 but that made water behind quartz glass super visible in the night
	// Was * 0.5 but that made water columns hardly visible
	float accuWeight = 1;
	
#if FOAMEFFECT > 0
	if (rgbaFinal.a > 0) {
	
		// Water edge + shinyness shading effect, kinda nice
		float ownDepth = linearDepth(gl_FragCoord.z);
		// Optimum: 3x3 grid (range=1) instead of 5x5 (range=2).
		// Saves 16 depth texture reads per water fragment (25→9). Foam edges stay sharp.
		float diffTotal = 0;
		int range = 1;
		for (int dx = -range; dx <= range; dx++) {
			for (int dy = -range; dy <= range; dy++) {
				float diff = ownDepth - linearDepth(texture(depthTex, vec2(x + dx/frameSize.x, y + dy/frameSize.y)).x);
				if (diff < 0.001) { // This check prevents foam not rendered when looking through grass at distant water
					diffTotal += abs(diff);
				}
			}
		}
		
		diffTotal /= 4;
		diffTotal = min(diffTotal, -vn/10 + 0.05);
		
		if (isLava) {
			float intensity = clamp(dot(fragNormal, vec3(0, 1, 0)), 0, 1) * 0.5;
			float a = fragWorldPos.x + fragWorldPos.y - 1.5 * flowVectorf.x * wfc;
			float b = fragWorldPos.z - 1.5 * flowVectorf.y * wfc;
			
			float diff = intensity * clamp(1 - diffTotal*1000 - gnoise(vec3(a*35, b*35, wfc))/2 + gnoise(vec3(a*2, b*2, wfc))/2, 0, 1);
			float noise = intensity * (gnoise(vec3(a, b, wfc)) + 0.5) / 2;
			float rgbAdd = bright*(diff * 0.3 + noise/10);
			texColor.rgb -= vec3(rgbAdd, rgbAdd, rgbAdd);
			texColor.a=1; // Let's just do lava as opaque as we can
			accuWeight=1;
			
			float blackSpots = gnoise(fragWorldPos.xyz) + 0.5;
			
			texColor.rgb -= blackSpots * 1.5 * 0.5;
			texColor.g += 0.2 * 0.5;
			
		} else {
			// Cold liquids
			vec3 localPos = fragWorldPos.xyz;
			
			// Foam
			
			float intensity = clamp(dot(fragNormal, vec3(0, 1, 0)), 0, 1);
			
			float a = localPos.x + localPos.y - 1.5 * flowVectorf.x * wfc;
			float b = localPos.z - 1.5 * flowVectorf.y * wfc;
			
			// without the -abs() one diagonal direction goes derpy noise
			float noise1 = gnoise(vec3(a*15, -abs(b*15), wfc)) + gnoise(vec3(a*5, b*5, wfc));
			float noise2 = gnoise(vec3(a, b, wfc));
			
			float diff = intensity * clamp(1 - diffTotal*1500 - noise1/2 + noise2/2, 0, 1);
			float noise = intensity * (gnoise(vec3(a * 0.4, b * 0.4, wfc))/2 + gnoise(vec3(a, b, wfc))/2 + 0.5) / 2;
			
			float rgbAdd = max(0, bright*(diff * 0.3 + noise/10));
			if (doLightFoam) {
				rgbAdd *= 0.5;
			}
			
			texColor.rgb += vec3(rgbAdd, rgbAdd, rgbAdd);
			texColor.a += (max(0, diff/16 + noise/(12 - 8*min(1,windSpeed))) + vn / 4) / clamp(fresnel, 0.5, 1);

	
			
			// Droplet noise
			float f = 0;
			if (skyExposed > 0) {
				vec2 uv = localPos.xz * (5 + noise1/20000.0);
				f = dropletnoise(uv);
			}
			
			

			// Specular reflection
			#if SHADOWQUALITY == 0
			if (skyExposed > 0) {
			#endif
				vec3 noisepos = vec3(localPos.x , localPos.z, waterWaveCounter / 8 + windWaveCounter / 6);
				
				//float dy = clamp(noise2 / 10, 0, 1) + gnoise(noisepos); - trippy specular rings
				
				float dy = noise2 / 20 + clamp(gnoise(noisepos) / 10, 0, 0.6);
				
				vec3 normal = normalize(vec3(dy, 1, -dy));
				
				float upness = max(0, dot(fragNormal, vec3(0,1,0))); // Only do specular reflections on up faces
				
				vec3 eye = normalize(vec3(fWorldPos.x, fWorldPos.y - 2, fWorldPos.z));
				vec3 reflectionVec = reflect(sunPosRel, normal);
				float p = dot(reflectionVec, eye);
				if (p > 0) {
					float sunb = clamp(sunPosRel.y * 10, 0, 1) * clamp(1.5 - sunPosRel.y, 0, 1) * sunSpecularIntensity;
					
					float specular = pow(p, 50) * sunb;
					
					#if SHADOWQUALITY > 0
					float weight = upness * clamp(specular * clamp(pow(shadowBright, 4), 0, 1) * clamp(1.5 * shadowIntensity, 0, 1), 0, 1);
					#else
					float weight = upness * clamp(specular * clamp(pow(shadowBright, 4), 0, 1) * clamp(1.5, 0.0, 1.0), 0, 1);
					#endif
					
					vec3 sunColf = applyFog(vec4(reflectColor, 1), fogAmount).rgb;
					
					texColor.rgb = mix(texColor.rgb, sunColf + noise1 * 0.2, weight);
					texColor.a = mix(texColor.a, texColor.a + specular/2, weight);
				}
			#if SHADOWQUALITY == 0
			}
			#endif
			
			texColor.rgb *= 1 + f;
			texColor.a += max(0, 0.5 - texColor.a)*0.5*f; // Some extra alpha for droplet noise where there is low alpha
			
		}
	}
	
#else
	if (isLava) {
		texColor.a=1;
		accuWeight=1;
	}
#endif
    
	
	texColor = applyFog(texColor, fogAmount);
	texColor.a = clamp(texColor.a + fogAmount, 0, 1);

	texColor = applySpheresFog(texColor, fogAmount, fWorldPos.xyz);

	OIT(texColor, glowLevel);
	
}
