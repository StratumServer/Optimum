#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of blockhighlights.fsh (docs/vulkan-native-shaders.md).
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
#include "blockhighlights.interface.glsl"
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
