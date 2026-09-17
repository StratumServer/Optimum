// Program interface of sky (docs/vulkan-native-shaders.md section 4). One draw per Use() and no sampler of
// its own (sky and glow are set 0 frame textures), so there is no push block; every uniform is a record
// member: sky.vsh's, sky.fsh's, then fogandlight.fsh's windWaveCounter (vertexwarp.vsh, its owner, is not
// included) and underwatereffects.fsh's frameSize.
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;

    float fogDensityIn;
    float fogMinIn;
    float dayLight;
    float horizonFog;
    vec3 playerPos;
    vec3 sunPosition;

    float windWaveCounter;
    vec2 frameSize;
};
