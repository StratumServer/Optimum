// Program interface of woittest (docs/vulkan-native-shaders.md section 4). No samplers, so no push block;
// woittest.vsh's two matrices are the record.
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
};
