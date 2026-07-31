#version 330 core

uniform sampler2DArray mapPages;

in vec2 texCoord;
flat in float layerIndex;

out vec4 outColor;

void main(void)
{
    outColor = texture(mapPages, vec3(texCoord, layerIndex));
}
