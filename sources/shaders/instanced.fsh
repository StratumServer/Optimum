#version 330 core
// Optimum override of the vanilla instanced.fsh: writes the TAA motion
// attachment for the instanced mechanical-power renderers (P3). Everything else
// is vanilla, line for line.
//
// instanced has no ALLOWDEPTHOFFSET variant, so the writer depth is plain
// gl_FragCoord.z - the same space the resolve compares against the depth buffer.
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
in vec4 fragPosition;
in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


uniform sampler2D tex;
uniform float alphaTest = 0.1;

#if TAAMOTION > 0
in vec4 taaPrevClip;
in float taaInstanceReactive;
uniform vec2 taaRenderSize;
uniform vec2 taaJitterPx;
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;

vec4 taaMotionVector(float reactive)
{
	if (taaPrevClip.w <= 1e-6) return vec4(0.0);
	vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;
	vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;
	return vec4(prevPixel - currentPixel, reactive, gl_FragCoord.z);
}
#endif

#include fogandlight.fsh

void main () {
  outColor = texture(tex, uv) * color;
  if (outColor.a < alphaTest) discard;
  
  outColor = applyFogAndShadowWithNormal(outColor, fogAmount, normal, 1, 0.45, worldPos.xyz);
  
  //outColor = vec4((normal.x + 0.5) / 2, (normal.y + 0.5)/2, (normal.z+0.5)/2, 1);

  outGlow = vec4(glowLevel, 0, 0, outColor.a);
  
#if SSAOLEVEL > 0
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = gnormal;
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif

#if TAAMOTION > 0
	// Mechanical blocks are opaque and not reactive on their own; the C# side
	// stamps reactive 1 per instance when its history was unusable, where the
	// vector above is camera-only and the history must not be trusted.
	outMotion = taaMotionVector(taaInstanceReactive);
#endif

}  