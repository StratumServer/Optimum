// Native port of the game include dither.fsh (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: dither.fsh
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_DITHER_GLSL
#define OPTIMUM_INCLUDE_DITHER_GLSL

// NoiseFromPixelPosition's last two parameters carry the names of skycolor.fsh's frame members.
// Where skycolor's names are active they are macros, which would rewrite the parameter
// declarations, so they are lifted around the function and restored after it; the function
// reads its parameters, exactly as the GLSL 330 one did.
#ifdef OPTIMUM_FRAME_NAMES_SKYCOLOR_FSH
#undef ditherSeed
#undef horizontalResolution
#endif

// Excellent dither method by Krishty
// http://old.zfx.info/DisplayThread.php?TID=24491
vec4 NoiseFromPixelPosition(ivec2 PixelsPosition, int ditherSeed, int horizontalResolution) {

    int PixelsIndex = horizontalResolution * PixelsPosition.y + PixelsPosition.x;
    int PixelsArea = PixelsPosition.x * PixelsPosition.y;

    ivec4 vPixelsIndex = ditherSeed + PixelsIndex * ivec4(41, 29, 53, 43);
    ivec4 vPixelsArea = ditherSeed + PixelsArea * ivec4(23, 59, 47, 37);

    return (vec4((vPixelsIndex ^ vPixelsArea) % 661) / 330.5 - 1.0) / 128;
}

#ifdef OPTIMUM_FRAME_NAMES_SKYCOLOR_FSH
#define ditherSeed optimumFrame.ditherSeed
#define horizontalResolution optimumFrame.horizontalResolution
#endif

#endif
