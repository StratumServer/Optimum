// Native port of the game include vertexflagbits.ash (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: vertexflagbits.ash
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_VERTEXFLAGBITS_GLSL
#define OPTIMUM_INCLUDE_VERTEXFLAGBITS_GLSL

// For most passes, these are the flag bits. For the liquid pass, the wind mode bits are used differently

// Bit 0..7
const int GlowLevelBitMask = 0xFF;

// Bit 8..10
const int ZOffsetBitMask = 0x7 << 8;

// Bit 11
const int ReflectiveBitMask = 1 << 11;

// Bit 12
const int Lod0BitMask = 1 << 12;

// Bit 13..25
const int NormalBitMask = 0xFFF << 13;

// Bit 25..28
const int WindModeBitMask = 0xF << 25;

const int WindModePosition = 25;

// Bit 25..27
const int LiquidWaterModeBitMask = 0xF << 25;

// Bit 29
const int LiquidExposedToSkyBitMask = 1 << 29;

// Bit 29..31
const int WindDataBitMask = 0x7 << 29;

const int WindDataPosition = 29;

// Bit 26..31
const int WindBitsMask = WindModeBitMask | WindDataBitMask;


const int WindModeWeakMask = 1 << 25;
const int WindModeNormalMask = 2 << 25;
const int WindModeLeavesMask = 3 << 25;
const int WindModeBendMask = 4 << 25;
const int WindModeTallBendMask = 5 << 25;
const int WindModeWaterMask = 6 << 25;
const int WindModeExtraWeakMask = 7 << 25;
const int WindModeFruitMask = 8 << 25;
const int WindModeWeakWindNoBendMask = 9 << 25;
const int WindModeWeakWindInversedBendMask = 10 << 25;
const int WindModeWaterPlant = 11 << 25;
const int WindModeLiquidWarp = 12 << 25;
const int WindModeWeakLowAlphaTest = 13 << 25;


// We use the wind data bits as the reflective mode. This value is the shape element face Reflective mode minus 1
// This unfortunately means we can't have something reflective *and* wind affected
const int ReflectiveModeWeak = 0;
const int ReflectiveModeMedium = 1;
const int ReflectiveModeStrong = 2;
const int ReflectiveModeSparkly = 3;
const int ReflectiveModeMild = 4;

// Liquid shader
const int LiquidIsLavaBitPosition = 27;
const int LiquidWeakFoamBitPosition = 28;
const int LiquidWeakWavePosition = 29;
const int LiquidFullAlphaBitPosition = 30;
const int LiquidSkyExposedBitPosition = 31;


// Bit 27
const int LiquidIsLavaBitMask = 1 << LiquidIsLavaBitPosition;
// Bit 28
const int LiquidWeakFoamBitMask = 1 << LiquidWeakFoamBitPosition;
// Bit 29
const int LiquidWeakWaveBitMask = 1 << LiquidWeakWavePosition;
// Bit 30
const int LiquidFullAlphaBitMask = 1 << LiquidFullAlphaBitPosition;
// Bit 31
const int LiquidSkyExposedBitMask = 1 << LiquidSkyExposedBitPosition;


// Because multiply is sometimes faster than divide (especially if the compiler can MAD)
const float OneOver255 = 1.0 / 255.0;


vec3 unpackNormal(int flags) {
	int x = (flags >> (13+1)) & 0x7;
	int y = (flags >> (13+5)) & 0x7;
	int z = (flags >> (13+9)) & 0x7;

	int signx = (flags >> 12) & 2;
	int signy = (flags >> (12+4)) & 2;
	int signz = (flags >> (12+8)) & 2;

	return normalize(vec3(
		(1.0 - signx) * x / 7.0,
		(1.0 - signy) * y / 7.0,
		(1.0 - signz) * z / 7.0
	));
}


struct FaceData {
	  // if modifying, vec3s should be 16-byte aligned
	vec3 xyz;
	int uv;
	vec3 xyzA;
	int uvSize;
	ivec4 flags;
	vec3 xyzB;
// Bits 0..7 = season map index
// Bits 8..11 = climate map index
// Bits 12 = Frostable bit
// Bits 13, 14, 15 = If a windmode is set, these 3 bits are used to offset the season position for more varied leaf colors
// Bits 16-23 = temperature
// Bits 24-31 = rainfall
        int colormapData;
};


vec2 UnpackUv(FaceData vdata, int vIndex, float subpixelPaddingX, float subpixelPaddingY) {
	int uvs = vdata.uvSize;
	int uvRotate = (uvs & 0x8000) >> 15;
	vec2 duv = vec2(
		((uvs & 0x7FFF)       - ((uvs & 0x4000) << 1) - 0.00000001) * ((vIndex + uvRotate) % 4 / 2),
		((uvs >> 16 & 0x7FFF) - ((uvs & 0x40000000) >> 15) - 0.00000001) * ((vIndex + 1 - uvRotate & 3) / 2)
	);
	return (vec2(vdata.uv & 0xFFFF, vdata.uv >> 16 & 0xFFFF) + duv) / 32768.0 - vec2(subpixelPaddingX * sign(duv.x), subpixelPaddingY * sign(duv.y));
}

#endif
