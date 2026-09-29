#version 330 core

// Optimum (Vulkan foundation, world/UI separation): the UI compose pass - the UI image over
// the display image, under premultiplied-alpha blending (ONE, ONE_MINUS_SRC_ALPHA):
//
//     dst = ui.rgb + dst.rgb * (1 - ui.a)
//
// The UI image already holds premultiplied colour: the GUI draws into it with the RGB factors
// it always had (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) over transparent black, which accumulates the
// over-operator's premultiplied result, while the alpha channel accumulates coverage under the
// separate (ONE, ONE_MINUS_SRC_ALPHA) factors every Standard-blended draw into that image uses.
// So this pass passes the texel through untouched and lets the blend stage do the operator.
//
// Why not blit.fsh: it ends with "outColor.a = 1", right for a blit onto an opaque backbuffer
// and fatal here - a forced alpha of 1 makes every UI texel cover the scene completely.

uniform sampler2D uiTex;

in vec2 texCoord;

out vec4 outColor;

void main(void)
{
	outColor = texture(uiTex, texCoord);
}
