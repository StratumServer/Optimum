// Program interface of cloudmap (docs/vulkan-native-shaders.md section 4). A fullscreen pass: one draw per
// Use(), so the push block holds only the sampler slots, in cloudmap.fsh's declaration order, and every
// other uniform is a record member: cloudmap.fsh's own, then the program uniforms of fogandlight.fsh and
// fogspheres.ash, whose frame owners (fogandlight.vsh, vertexwarp.vsh) this program does not include.
//
// pointLightQuantity, pointLights, pointLightColors and nightVisionStrength are cloudmap.fsh's own uniforms
// (it declares them itself), not frame members. The arrays are sized by FrameGlobals.MaxDynamicLights (100),
// as the frame block's copies are (section 5); pointLightQuantity bounds the loop.
layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, mapData1);
    OPTIMUM_SAMPLER_SLOT(sampler2D, mapData2);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
{
    float dayLight;
    float globalCloudBrightness;
    float time;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    vec3 sunPosition;
    float nightVisionStrength;
    float alpha;

    float width;
    vec3 mapOffset;
    vec2 mapOffsetCentre;
    mat4 viewMatrix;

    int pointLightQuantity;
    vec3 pointLights[100];
    vec3 pointLightColors[100];

    float flatFogDensity;
    float flatFogStart;
    float viewDistance;
    float viewDistanceLod0;
    float windWaveCounter;
    float fogSpheres[24];
    int fogSphereQuantity;
};
