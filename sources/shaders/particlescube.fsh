#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Optimum override of the vanilla particlescube.fsh (TAA P4). Vanilla is
// untouched; the motion attachment is written beside its outputs and the whole
// addition preprocesses away when TAAMOTION is 0.

in vec4 color;
in vec2 uv;
in float glowLevel;
in float fogAmount;
in vec4 rgbaFog;
in vec3 normal;
in vec4 worldPos;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
in vec4 fragPosition;
in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

// TAA motion vectors (Optimum P4). TAAMOTIONLOCATION is the Primary colour
// attachment the motion texture occupies (2 without the SSAO G-buffer, 4 with
// it); SystemRenderParticles opens the draw-buffer window that lets this
// attachment be written at all, and the blend seam forces replace blending on
// it - a blended motion vector averages two surfaces' displacements and belongs
// to neither.
#if TAAMOTION > 0
in vec4 taaPrevClip;
uniform vec2 taaRenderSize;   // render-target size in pixels
uniform vec2 taaJitterPx;     // this frame's sub-pixel shear, in pixels
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
#endif

#include fogandlight.fsh
#include underwatereffects.fsh

void main()
{
	#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	#else
	float intensity = 0.45;
	#endif

	

	float murkiness = getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(color, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);	
	} else {	
		outColor = applyFogAndShadowWithNormal(color, fogAmount, normal, 1, intensity, worldPos.xyz);
	}

	outGlow = vec4(glowLevel, 0, 0, outColor.a);
	//outColor = vec4((normal.x + 1) / 2.0, (normal.y + 1) / 2.0, (normal.z + 1) / 2.0, 1);
	
#if SSAOLEVEL > 0
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, outColor.a);
#endif

#if TAAMOTION > 0
	// b = 1: a cube particle is reactive, always. Its previous position is the
	// camera-only one (see the vertex shader), it is alpha-blended over whatever
	// was behind it, and it appears and disappears from one frame to the next -
	// three separate reasons why last frame's colour at the reprojected location
	// is not this particle. Reactive 1 makes the resolve take this frame's pixel
	// (taa-resolve.fsh: alpha = max(alpha, reactive)).
	//
	// a = gl_FragCoord.z: the writer depth. Cube particles are drawn with the
	// depth mask on, so this is the value that ends up in Primary's depth
	// attachment and the resolve's writer-depth test accepts the pixel. Without
	// it the reactive value would never be delivered.
	//
	// A previous position behind the previous camera is not a motion vector; a
	// zero alpha routes the pixel to the resolve's camera fallback, exactly as
	// in chunkopaque.fsh.
	if (taaPrevClip.w <= 1e-6) {
		outMotion = vec4(0.0);
	} else {
		vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;
		vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;
		outMotion = vec4(prevPixel - currentPixel, 1.0, gl_FragCoord.z);
	}
#endif
}