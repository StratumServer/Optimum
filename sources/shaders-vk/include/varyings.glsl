// Interface locations of the varyings the shared includes declare
// (docs/vulkan-native-shaders.md section 1).
//
// GLSL 330 matches varyings between stages by name; SPIR-V matches them by location, so
// an include's `out` in the vertex stage and the program's (or include's) `in` in the
// fragment stage must name the same number. The includes and the programs that read
// these varyings (a fragment stage's `in float glowLevel`, for example) use these
// defines; a program's own varyings use locations 0 to OPTIMUM_LOCATION_PROGRAM_END - 1.
//
// The block ends at location 25, inside the 29 fragment-input locations Mesa's Intel
// driver reports (maxFragmentInputComponents 116 on an ADL-S iGPU; NVIDIA reports 128).
// The Vulkan floor is 64 components (16 locations), which the device floor's
// descriptor-indexing requirements already rule out in practice.

#ifndef OPTIMUM_VARYINGS_GLSL
#define OPTIMUM_VARYINGS_GLSL

#define OPTIMUM_LOCATION_PROGRAM_END 16

// fogandlight.vsh / fogandlight.fsh
#define OPTIMUM_LOCATION_BLOCK_BRIGHTNESS 16
#define OPTIMUM_LOCATION_GLOW_LEVEL 17
#define OPTIMUM_LOCATION_BLOCK_LIGHT 18

// shadowcoords.vsh / fogandlight.fsh
#define OPTIMUM_LOCATION_SHADOW_COORDS_FAR 19
#define OPTIMUM_LOCATION_SHADOW_COORDS_NEAR 20

// colormap.vsh / colormap.fsh
#define OPTIMUM_LOCATION_CLIMATE_COLOR_MAP_UV 21
#define OPTIMUM_LOCATION_SEASON_COLOR_MAP_UV 22
#define OPTIMUM_LOCATION_SEASON_WEIGHT 23
#define OPTIMUM_LOCATION_HERETEMP 24
#define OPTIMUM_LOCATION_FROST_ALPHA 25

#endif
