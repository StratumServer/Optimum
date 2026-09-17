// Program interface of autocamera (docs/vulkan-native-shaders.md section 4). No samplers and no scalars: the two
// matrices are the whole record, and there is no push block. The includes' uniforms are frame members
// (autocamera includes shadowcoords.vsh and fogandlight.vsh, their owners).
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
};
