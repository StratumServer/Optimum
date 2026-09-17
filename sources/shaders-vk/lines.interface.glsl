// Program interface of lines (docs/vulkan-native-shaders.md section 4). No samplers, and the two matrices alone
// exceed the push budget, so there is no push block: every uniform is a record member, lines.vsh's then
// lines.fsh's, each in declaration order. glowLevel's GLSL 330 initializer (1.0) is seeded by the runtime.
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float lineWidth;
    mat4 projection;
    mat4 view;
    vec3 origin;

    vec4 color;
    float glowLevel;
};
