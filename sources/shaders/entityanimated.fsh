#version 330 core
// Optimum override of the vanilla entityanimated.fsh: adds the TAA motion-vector
// output (P3). Everything else is vanilla, line for line.
//
// Only the opaque (USEOIT == 0) variant writes motion: the OIT variant already
// fills six outputs on the Transparent framebuffer and never touches Primary.
// The varying itself is declared for both so the vertex and fragment interfaces
// match whichever way the program was compiled.
#extension GL_ARB_explicit_attrib_location: enable

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float fogAmount;
in float glowLevel;
in vec3 vertexPosition;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 blockLight;
in vec4 camPos;
in float damageEffect;
in float fragFrostAlpha;

// Our include system is dumb and does not do conditional includes
// So we add a OIT preprocceor test to oit.fsh as well
#include oit.fsh

#if USEOIT==0
	layout(location = 0) out vec4 outColor;
	layout(location = 1) out vec4 outGlow;
	#if SSAOLEVEL > 0
	in vec4 fragPosition;
	in vec4 gnormal;
	layout(location = 2) out vec4 outGNormal;
	layout(location = 3) out vec4 outGPosition;
	#endif
#endif

// TAA motion vectors (Optimum P3); see chunkopaque.fsh for the contract.
// The alpha channel is this fragment's WINDOW depth, which for the first-person
// hand and item programs is gl_FragCoord.z + depthOffset, not gl_FragCoord.z -
// they write gl_FragDepth below, and the resolve compares what it finds here
// against the depth buffer. Writing the un-offset value would make the resolve
// reject every hand pixel and fall back to camera reprojection on the one class
// of geometry whose motion differs most from the camera's.
#if TAAMOTION > 0
in vec4 taaPrevClip;
#if USEOIT==0
uniform vec2 taaRenderSize;
uniform vec2 taaJitterPx;
uniform float taaReactive = 0.0;
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;

vec4 taaMotionVector(float reactive, float writerDepth)
{
	if (taaPrevClip.w <= 1e-6) return vec4(0.0);
	vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;
	vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;
	return vec4(prevPixel - currentPixel, reactive, writerDepth);
}
#endif
#endif

uniform sampler2D entityTex;
uniform float alphaTest = 0.001;
uniform float glitchEffectStrength;
uniform int entityId;
uniform int glitchFlicker;
#if defined(ALLOWDEPTHOFFSET)
#if ALLOWDEPTHOFFSET > 0
uniform float depthOffset;
#endif
#endif

#include vertexflagbits.ash
#include fogandlight.fsh
#include noise3d.ash
#include noise2d.ash
#include underwatereffects.fsh

void main() {
	float b = 1;
	
	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	vec4 texColor = texture(entityTex, uv);
	
	#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	#else
	float intensity = 0.45;
	#endif
	
	
	//float seed = mod(entityId, 1000) / 5.0; - this is broken on NVIDIA cards O_O
	int eidfloor = (entityId / 100) * 100;
	float seed = (entityId - eidfloor) / 5.0;
		
	texColor = applyFrostEffect(fragFrostAlpha, texColor, normal, vertexPosition + vec3(seed));
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition + vec3(seed), 0);
	
	texColor *= color;
	texColor.rgb *= b;

#if USEOIT>0
	vec4 outColor;
#endif
	
	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(texColor, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);	
	} else {	
		outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity, worldPos.xyz);
	}
	
	
	if (glitchFlicker >0 && glitchEffectStrength > 0) {
		float g = gnoise(vec3(gl_FragCoord.y / 2.0, gl_FragCoord.x / 2.0, windWaveCounter*30 + entityId * 3));
		outColor.a *= mix(1, clamp(0.7 + g / 2, 0, 1), glitchEffectStrength);
		
		float b = gnoise(vec3(0, 0, windWaveCounter*60 + entityId * 3));
		outColor.a *= mix(1, clamp(b * 10 + 2, 0, 1), glitchEffectStrength);
	}

#if NORMALVIEW == 0
	if (outColor.a < alphaTest) discard;
#endif



	float glow = 0;
#if SHINYEFFECT > 0	
	outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1)), outColor, min(1, 2 * fogAmount));
#endif

#if USEOIT==0 && SSAOLEVEL > 0
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, 0);
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif

	

#if USEOIT > 0
	OIT(outColor, glowLevel+glow);
#else
	outGlow = vec4(glowLevel + glow, 0, 0, color.a);
#endif

	
	
#if defined(ALLOWDEPTHOFFSET) && ALLOWDEPTHOFFSET > 0
	// This likely tanks performance in any other scenario so we do only only for the first person mode rendering. See also https://www.khronos.org/opengl/wiki/Early_Fragment_Test#Limitations
	gl_FragDepth = gl_FragCoord.z + depthOffset;
	
	// A bit hacky: We use ALLOWDEPTHOFFSET for the first person rendering. SSAO seems to break on it, so we disable it
	#if USEOIT==0 && SSAOLEVEL > 0
		outGPosition.w=1;
	#endif
	
#endif


#if TAAMOTION > 0 && USEOIT==0
	// Opaque skinned entities are not reactive on their own; the C# side raises
	// taaReactive to 1 for a draw whose per-entity history was unusable, where
	// the vector above is camera-only and the history must not be trusted.
	#if defined(ALLOWDEPTHOFFSET) && ALLOWDEPTHOFFSET > 0
		outMotion = taaMotionVector(taaReactive, clamp(gl_FragCoord.z + depthOffset, 0.0, 1.0));
	#else
		outMotion = taaMotionVector(taaReactive, gl_FragCoord.z);
	#endif
#endif
}