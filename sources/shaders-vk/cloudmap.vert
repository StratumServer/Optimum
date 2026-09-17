#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of cloudmap.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "cloudmap.interface.glsl"

layout(location = 0) in vec2 p;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 ndc;

void main(){
    gl_Position = vec4(p, 0.0, 1.0);
    uv = p * 0.5 + 0.5;
    ndc = p;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
