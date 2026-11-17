#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec4 color;
in vec2 uv;
in vec4 rgbaFog;
in float fogAmount;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in float normalShadeIntensity;

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


uniform sampler2D tex;
uniform float alphaTest = 0.1;

#include fogandlight.fsh

void main () {
  outColor = texture(tex, uv) * color;
  if (outColor.a < alphaTest) discard;
  
  outColor = applyFogAndShadowWithNormal(outColor, fogAmount, normal, 1, 0.45, worldPos.xyz);
  
  //outColor = vec4((normal.x + 0.5) / 2, (normal.y + 0.5)/2, (normal.z+0.5)/2, 1);

  outGlow = vec4(glowLevel, 0, 0, outColor.a);
  
#if SSAOLEVEL > 0
#if defined(SSAO_DEPTH_RECONSTRUCT) && SSAO_DEPTH_RECONSTRUCT > 0
	outGNormal = vec4(optimumOctEncodeNormal(gnormal.xyz), fogAmount + glowLevel, gnormal.w);
#else
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = gnormal;
#endif
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif

}  