#version 330 core
// Optimum TAA debug views (P1). Reads the Primary motion attachment, depth
// buffer and resolved/raw scene colour and visualises them for the developer
// debug switch OptimumConfig.TaaDebugView. Does not affect the normal blit
// path; only reached when a debug mode is selected and the motion
// attachment exists.

uniform sampler2D motionTex;
uniform sampler2D depthTex;
uniform sampler2D sceneTex;
uniform int mode;
uniform vec2 renderSize;

in vec2 texCoord;

layout(location = 0) out vec4 outColor;

void main(void)
{
	ivec2 pixel = ivec2(clamp(texCoord * renderSize, vec2(0.0), renderSize - vec2(1.0)));

	// Motion attachment: rg = mv (render-resolution px, jitter excluded),
	// b = reactive, a = writerDepth (NDC depth at write time, 0 when unwritten).
	vec4 motion = texelFetch(motionTex, pixel, 0);
	float sceneDepth = texelFetch(depthTex, pixel, 0).r;

	if (mode == 1)
	{
		// Motion as colour: map +/-16px to the full 0..1 range per channel.
		vec2 mapped = clamp(motion.rg / 16.0, vec2(-1.0), vec2(1.0)) * 0.5 + 0.5;
		outColor = vec4(mapped, 0.0, 1.0);
	}
	else if (mode == 2)
	{
		// Reactive mask (b channel) as greyscale.
		outColor = vec4(vec3(motion.b), 1.0);
	}
	else if (mode == 3)
	{
		// Validity: green where the writer's recorded depth still matches the
		// final depth buffer, red where it does not (occluded/overwritten,
		// falls back to camera-motion reprojection), black where nothing wrote
		// motion for this pixel at all.
		if (motion.a == 0.0)
		{
			outColor = vec4(0.0, 0.0, 0.0, 1.0);
		}
		// Same tolerance as taa-resolve.fsh's `written` test: half precision on
		// the RGBA16F alpha costs ~5e-4 near 1.0, so a fixed 1e-4 here reported
		// mismatches for writers the resolve pass happily accepts.
		else if (abs(motion.a - sceneDepth) <= max(2e-4, 8e-4 * sceneDepth))
		{
			outColor = vec4(0.0, 1.0, 0.0, 1.0);
		}
		else
		{
			outColor = vec4(1.0, 0.0, 0.0, 1.0);
		}
	}
	else if (mode == 4)
	{
		// Scene colour with a motion-vector colour overlay blended on top.
		vec3 scene = texture(sceneTex, texCoord).rgb;
		vec2 mapped = clamp(motion.rg / 16.0, vec2(-1.0), vec2(1.0)) * 0.5 + 0.5;
		vec3 overlay = vec3(mapped, 0.0);
		outColor = vec4(mix(scene, overlay, 0.5), 1.0);
	}
	else
	{
		outColor = texture(sceneTex, texCoord);
	}
}
