// Program interface of instanced (docs/vulkan-native-shaders.md section 4). Every per-object value travels as
// an instance attribute (locations 4-13), and the uniforms are set once per Use(), so the push block holds only
// the sampler slot and every uniform is a record member: instanced.vsh's, then instanced.fsh's, then
// fogandlight.frag.glsl's windWaveCounter (no vertexwarp owner in this program), each in declaration order.
//
// A block member cannot carry alphaTest's GLSL 330 initializer (0.1); the runtime seeds it (section 8).
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, tex);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    vec4 rgbaFogIn;
    vec3 rgbaAmbientIn;
    float fogMinIn;
    float fogDensityIn;
    mat4 projectionMatrix;
    mat4 modelViewMatrix;
    mat4 prevProjectionMatrix;
    mat4 prevModelViewMatrix;
    vec3 cameraPosDelta;

    float alphaTest;
    vec2 taaRenderSize;
    vec2 taaJitterPx;

    float windWaveCounter;
};
