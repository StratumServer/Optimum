// Specialization constants for Optimum's Vulkan-native shaders
// (docs/vulkan-native-shaders.md section 5).
//
// This file is the source of truth for constant ids. The renderer's
// Shaders/SpecializationConvention.cs mirrors it, and SpecializationConventionTests
// fails when the two disagree or when a constant no longer matches a define
// ShaderRegistry.registerDefaultShaderCodePrefixes stamps.
//
// Each constant replaces the quality or code-path define of the same name: a native
// source writes `if (OPTIMUM_BLOOM != 0)` where the GLSL 330 source has `#if BLOOM > 0`,
// and everything the branch uses is declared unconditionally. Defaults are 0, the value
// an undefined macro has in `#if`; the runtime specializes every constant.

#ifndef OPTIMUM_SPECIALIZATION_GLSL
#define OPTIMUM_SPECIALIZATION_GLSL

layout(constant_id = 0) const int OPTIMUM_FXAA = 0;
layout(constant_id = 1) const int OPTIMUM_SSAOLEVEL = 0;
layout(constant_id = 2) const int OPTIMUM_NORMALVIEW = 0;
layout(constant_id = 3) const int OPTIMUM_BLOOM = 0;
layout(constant_id = 4) const int OPTIMUM_GODRAYS = 0;
layout(constant_id = 5) const int OPTIMUM_FOAMEFFECT = 0;
layout(constant_id = 6) const int OPTIMUM_SHINYEFFECT = 0;
layout(constant_id = 7) const int OPTIMUM_SHADOWQUALITY = 0;
layout(constant_id = 8) const int OPTIMUM_WAVINGSTUFF = 0;
layout(constant_id = 9) const float OPTIMUM_MINBRIGHT = 0.0;
layout(constant_id = 10) const int OPTIMUM_GREEDYMESH_GRAD = 0;
// DYNLIGHTS no longer sizes the point-light arrays (fixed at FrameGlobals.MaxDynamicLights;
// pointLightQuantity bounds the loop). Its zero value still selects fogandlight's
// no-point-light path, so the value stays a constant.
layout(constant_id = 11) const int OPTIMUM_DYNLIGHTS = 0;
// Optimum AO: the class-channel writes and scene-ssao's GTAO compose branch. Gates no
// output or varying, so it is a constant, not an axis.
layout(constant_id = 12) const int OPTIMUM_OPTIMUMAO = 0;

#endif
