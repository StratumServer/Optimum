#version 330 core

// Per-vertex attributes (instanced quad)
layout(location = 0) in vec2 vertexPos;    // quad corner: (0,0), (1,0), (1,1), (0,1)
layout(location = 1) in vec4 instanceRect; // xy = screen pos, zw = screen size (per instance)
layout(location = 2) in float instanceLayer; // texture array layer (per instance)

uniform vec2 screenSize; // viewport width, height
uniform float zValue;    // NDC Z matching vanilla renderZ=50 in ortho projection

out vec2 texCoord;
flat out float layerIndex;

void main(void)
{
    // Compute screen-space position for this vertex
    vec2 screenPos = instanceRect.xy + vertexPos * instanceRect.zw;

    // Convert screen pixels to NDC: [0, screenSize] -> [-1, 1]
    vec2 ndc = (screenPos / screenSize) * 2.0 - 1.0;
    ndc.y = -ndc.y; // flip Y (screen coords: top-left origin, NDC: bottom-left origin)

    gl_Position = vec4(ndc, zValue, 1.0);
    texCoord = vertexPos;
    layerIndex = instanceLayer;
}
