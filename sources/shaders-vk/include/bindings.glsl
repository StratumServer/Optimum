// Descriptor set convention for Optimum's Vulkan-native shaders (plan decision 9):
// one pipeline layout shared by every program.
//
// This file is the source of truth for set and binding numbers. The renderer's
// Shaders/SetConvention.cs mirrors it, and SetConventionTests fails when the two
// disagree on any define or declaration.
//
//   set 0  frame     once per frame        FrameGlobals UBO (dynamic offset) + frame textures
//   set 1  textures  on create / retire    bindless combined-image-sampler arrays,
//                                          PARTIALLY_BOUND | UPDATE_AFTER_BIND
//   set 2  storage   per draw              FaceData, the animation buffers, the program record
//                                          and named blocks
//   push   per draw                        texture slot indices and per-draw scalars
//
// Indices into the set-1 arrays come from push constants and are uniform over a
// draw, so they need no nonuniformEXT; an index taken from per-vertex, per-instance
// or per-pixel data does (docs/research/vulkan-bindless.md, section 4).

#ifndef OPTIMUM_BINDINGS_GLSL
#define OPTIMUM_BINDINGS_GLSL

#extension GL_EXT_nonuniform_qualifier : require

#define OPTIMUM_SET_FRAME 0
#define OPTIMUM_SET_TEXTURES 1
#define OPTIMUM_SET_STORAGE 2

#define OPTIMUM_PUSH_CONSTANT_BYTES 128

// A sampler's slot index in the push block, under the GLSL 330 sampler's own name:
//   OPTIMUM_SAMPLER_SLOT(sampler2DArray, terrainTex);
// declares `uint terrainTex`. SPIR-V keeps no trace of which array a uint indexes, so the
// offline compiler reads the type from this declaration and checks it against the set 1
// array the shipped module actually indexes (docs/vulkan-native-shaders.md section 4).
#define OPTIMUM_SAMPLER_SLOT(glslType, name) uint name

// Set 0. The FrameGlobals block itself is generated from Shaders/FrameGlobals.cs.
#define OPTIMUM_BINDING_FRAME_GLOBALS 0
#define OPTIMUM_BINDING_SHADOW_MAP_FAR 1
#define OPTIMUM_BINDING_SHADOW_MAP_NEAR 2
#define OPTIMUM_BINDING_SKY 3
#define OPTIMUM_BINDING_GLOW 4
#define OPTIMUM_BINDING_LIQUID_DEPTH 5

layout(set = OPTIMUM_SET_FRAME, binding = OPTIMUM_BINDING_SHADOW_MAP_FAR) uniform sampler2DShadow shadowMapFar;
layout(set = OPTIMUM_SET_FRAME, binding = OPTIMUM_BINDING_SHADOW_MAP_NEAR) uniform sampler2DShadow shadowMapNear;
layout(set = OPTIMUM_SET_FRAME, binding = OPTIMUM_BINDING_SKY) uniform sampler2D sky;
layout(set = OPTIMUM_SET_FRAME, binding = OPTIMUM_BINDING_GLOW) uniform sampler2D glow;
layout(set = OPTIMUM_SET_FRAME, binding = OPTIMUM_BINDING_LIQUID_DEPTH) uniform sampler2D liquidDepth;

// Set 1. Capacities are the starting sizes from docs/research/vulkan-bindless.md;
// slot 0 of every array holds a placeholder.
#define OPTIMUM_BINDING_TEXTURES_2D 0
#define OPTIMUM_BINDING_TEXTURES_2D_ARRAY 1
#define OPTIMUM_BINDING_TEXTURES_CUBE 2
#define OPTIMUM_BINDING_TEXTURES_3D 3
#define OPTIMUM_BINDING_TEXTURES_2D_UINT 4
#define OPTIMUM_BINDING_TEXTURES_2D_INT 5
#define OPTIMUM_BINDING_TEXTURES_2D_SHADOW 6
#define OPTIMUM_BINDING_TEXTURES_2D_ARRAY_SHADOW 7
#define OPTIMUM_BINDING_TEXTURES_CUBE_SHADOW 8

#define OPTIMUM_CAPACITY_TEXTURES_2D 16384
#define OPTIMUM_CAPACITY_TEXTURES_2D_ARRAY 1024
#define OPTIMUM_CAPACITY_TEXTURES_CUBE 256
#define OPTIMUM_CAPACITY_TEXTURES_3D 256
#define OPTIMUM_CAPACITY_TEXTURES_2D_UINT 1024
#define OPTIMUM_CAPACITY_TEXTURES_2D_INT 256
#define OPTIMUM_CAPACITY_TEXTURES_2D_SHADOW 128
#define OPTIMUM_CAPACITY_TEXTURES_2D_ARRAY_SHADOW 64
#define OPTIMUM_CAPACITY_TEXTURES_CUBE_SHADOW 64

layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_2D) uniform sampler2D optimumTextures2D[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_2D_ARRAY) uniform sampler2DArray optimumTextures2DArray[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_CUBE) uniform samplerCube optimumTexturesCube[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_3D) uniform sampler3D optimumTextures3D[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_2D_UINT) uniform usampler2D optimumTextures2DUint[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_2D_INT) uniform isampler2D optimumTextures2DInt[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_2D_SHADOW) uniform sampler2DShadow optimumTextures2DShadow[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_2D_ARRAY_SHADOW) uniform sampler2DArrayShadow optimumTextures2DArrayShadow[];
layout(set = OPTIMUM_SET_TEXTURES, binding = OPTIMUM_BINDING_TEXTURES_CUBE_SHADOW) uniform samplerCubeShadow optimumTexturesCubeShadow[];

// Set 2. The blocks' contents are declared by the programs that read them.
#define OPTIMUM_BINDING_FACE_DATA 0
#define OPTIMUM_BINDING_ANIMATION 1
#define OPTIMUM_BINDING_ANIMATION_PREV 2
// The program record (docs/vulkan-native-shaders.md section 4): a dynamic uniform
// buffer with every non-frame uniform that is not in the push block.
#define OPTIMUM_BINDING_PROGRAM_RECORD 3
// Any other named block a rewritten (mod or GLSL 330) program declares, as a
// layout(std140) readonly storage buffer, in declaration order. More fails the link.
#define OPTIMUM_BINDING_NAMED_BLOCK_FIRST 4
#define OPTIMUM_BINDING_NAMED_BLOCK_LAST 7

#endif
