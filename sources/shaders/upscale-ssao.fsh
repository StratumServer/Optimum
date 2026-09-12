#version 330 core
uniform sampler2D ssaoScene;
uniform float invRenderHeight;
in vec2 texCoord;
layout(location = 0) out vec4 outColor;
void main()
{
    float ao = texture(ssaoScene, texCoord).r;
    #if SSAOLEVEL > 1
        ao = min(ao, texture(ssaoScene, texCoord - vec2(0.0, invRenderHeight)).r);
    #endif
    // EnumBlendMode.Multiply: dstRGB * (1 - srcAlpha). RGB is not read.
    outColor = vec4(0.0, 0.0, 0.0, 1.0 - clamp(ao, 0.0, 1.0));
}
