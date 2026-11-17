#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
#if !defined(SSAO_DEPTH_RECONSTRUCT) || SSAO_DEPTH_RECONSTRUCT == 0
in vec4 fragPosition;
layout(location = 3) out vec4 outGPosition;
#endif
in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
#endif

#if SSAOLEVEL > 0 && defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
vec2 optimumOctEncodeNormal(vec3 n) {
	float lenSq = dot(n, n);
	if (lenSq <= 1e-10) {
		return vec2(0.0);
	}
	n *= inversesqrt(lenSq);
	vec2 p = n.xy * (1.0 / (abs(n.x) + abs(n.y) + abs(n.z)));
	if (n.z < 0.0) {
		p = (1.0 - abs(p.yx)) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
	}
	return p;
}
#endif


uniform sampler2D itemTex;
uniform float alphaTest = 0.001;

// Texture overlay "hack"
// We only have the base texture UV coordinates, which, for blocks and items in inventory is the block or item texture atlas, but none uv coords for a dedicated overlay texture
// So lets remove the base offset (baseUvOrigin) and rescale the coords (baseTextureSize / overlayTextureSize) to get useful UV coordinates for the overlay texture
uniform sampler2D tex2dOverlay;
uniform float overlayOpacity;
uniform vec2 overlayTextureSize;
uniform vec2 baseTextureSize;
uniform vec2 baseUvOrigin;
uniform int normalShaded;
uniform float damageEffect = 0;

in vec2 uv;
in vec4 color;
in float glowLevel;
in float n;
in vec3 normal;
in vec3 vertexPosition;

#include vertexflagbits.ash
#include normalshading.fsh
#include noise2d.ash

void main () {
	float b = 1;
	
	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	if (overlayOpacity > 0) {
		vec2 uvOverlay = (uv - baseUvOrigin) * (baseTextureSize / overlayTextureSize);
	
		vec4 col1 = texture(tex2dOverlay, uvOverlay);
		vec4 col2 = texture(itemTex, uv);
		
		float a1 = overlayOpacity * col1.a  * min(1, col2.a * 100);
		float a2 = col2.a * (1 - a1);
		
		outColor = vec4(
			(a1 * col1.r + col2.r * a2) / (a1+a2),
			(a1 * col1.b + col2.g * a2) / (a1+a2),
			(a1 * col1.g + col2.b * a2) / (a1+a2),
			a1 + a2
		) * color;
		
	} else {
		outColor = texture(itemTex, uv) * color;
	}

	outColor.a = clamp(outColor.a, 0, 1); // No idea why, makes held torches glitchy without

#if BLOOM == 0
	outColor.rgb *= 1 + glowLevel;
#endif

	// Ensure held item always being in the front
	gl_FragDepth = gl_FragCoord.z / 20;
	
	if (outColor.a < alphaTest) discard;
	
	if (normalShaded > 0) {
		float b = min(1, getBrightnessFromNormal(normal, 1, 0.45) + glowLevel);
		outColor *= vec4(b, b, b, 1);
	}
	
#if SSAOLEVEL > 0
#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	outGNormal = vec4(optimumOctEncodeNormal(gnormal.xyz), 1.0, gnormal.w);
#else
	// Doesn't work properly for some reason
	//outGPosition = vec4(fragPosition.xyz, glowLevel);
	outGPosition = vec4(1);
	outGNormal = gnormal;
#endif
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif

	outColor.rgb *= b;
	
	outGlow = vec4(glowLevel, 0, 0, outColor.a);
}