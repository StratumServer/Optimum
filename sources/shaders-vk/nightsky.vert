#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of nightsky.vsh (docs/vulkan-native-shaders.md).
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "nightsky.interface.glsl"

layout(location = 0) in vec3 vertexPosition;

layout(location = 0) out vec3 texCoords;
layout(location = 2) out float nightVisionStrengthv;

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"

void main () {
  texCoords = vertexPosition;
  vec4 worldPos = modelMatrix * vec4(vertexPosition, 1.0);
  nightVisionStrengthv = nightVisionStrength * 0.33;

  gl_Position = projectionMatrix * viewMatrix * worldPos;

    // GL clip depth [-w, w] to Vulkan's [0, w]: the statement ShaderRewriter appends to every GLSL 330 vertex stage.
    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}
