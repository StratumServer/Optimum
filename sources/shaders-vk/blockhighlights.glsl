#version 450
#if defined(OPTIMUM_VERTEX)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blockhighlights.vsh (docs/vulkan.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#elif defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blockhighlights.fsh (docs/vulkan.md).
// fogandlight.fsh reads flatFogDensity, flatFogStart, viewDistance and viewDistanceLod0, owned by fogandlight.vsh,
// which the vertex stage includes, so this stage activates that owner group itself (section 3).
//
// oit.fsh declares its outputs and OIT() under #if USEOIT > 0, which makes USEOIT an axis of this program. The
// client always registers blockhighlights with Oit = true (ShaderProgramBase.Oit's default), so USEOIT=0 is a
// variant no client produces; its GLSL 330 stage would not compile (OIT undefined). The call is therefore
// guarded by the axis, and that variant writes nothing, exactly the outputs its GLSL 330 declarations have.
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

// Program interface of blockhighlights (docs/vulkan.md). The push block holds the
// sampler slot. The record holds the matrices and fogandlight.fsh's windWaveCounter, whose owner
// (vertexwarp.vsh) the program does not include; fogandlight.fsh's other program uniforms are frame members
// through fogandlight.vsh in the vertex stage.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, particleTex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float windWaveCounter;
};

#if defined(OPTIMUM_VERTEX)


layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;


layout(location = 0) out vec4 color;
layout(location = 1) out vec4 rgbaFog;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main(void)
{
	vec4 cameraPos = modelViewMatrix * vec4(vertexPositionIn, 1.0);

	color = vertexColor;
	gl_Position = projectionMatrix * cameraPos;

	// We are cheap. We pretend the highlights are closer to the camera to enforce it
	// always being drawn on top
	gl_Position.w += 0.0004;

	rgbaFog = vec4(0);
	glowLevel = 0;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)

#include "varyings.glsl"

layout(location = 0) in vec4 color;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 1) in vec4 rgbaFog;


#include "fogandlight.frag.glsl"
#include "oit.glsl"

void main()
{
#if USEOIT == 1
    OIT(color, glowLevel);
#endif
}

#endif
