// Native port of the game include oit.fsh (docs/vulkan.md).
// optimum-port-of: oit.fsh
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_OIT_GLSL
#define OPTIMUM_INCLUDE_OIT_GLSL

#if USEOIT > 0
#define OIT_BINS 3
#define OIT_BIN_SCALE 30.0

layout(location = 0) out vec4 OITreveal;
layout(location = 1) out vec4 outReveal;
layout(location = 2) out vec4 outGlow;
layout(location = 3) out vec4 OITaccumulation0;
layout(location = 4) out vec4 OITaccumulation1;
layout(location = 5) out vec4 OITaccumulation2;

void OITaccumulate(int bin, vec4 x){
    switch(bin){
        case 0:  OITaccumulation0 = x; return;
        case 1:  OITaccumulation1 = x; return;
        default: OITaccumulation2 = x;
    }
}

float OITbellcurve(float t){
    float n = t / 0.832;
    return exp(-n * n);
}

float OITweight(float t, float a){
    return exp(-t / 100.0);
}

void OIT(vec4 colour, float glow, float depth){

    depth /= OIT_BIN_SCALE;

    float bin = log(depth + 1.0);
    float w = OITweight(depth, colour.a);

    colour.rgb *= colour.a;

    for(int i = 0; i < OIT_BINS; i++){

        float b = OITbellcurve(bin - float(i));

        if(i == (OIT_BINS-1) && bin > float(OIT_BINS-1)) b = 1.0;

        OITaccumulate(i, colour * w * b);
        OITreveal[i] = 1.0 - colour.a * b;

    }

    outReveal = vec4(1.0 - colour.a);
    outGlow = vec4(glow, 0.0, 0.0, colour.a);

}

void OIT(vec4 colour, float glow){

    float depth = (gl_FragCoord.z * 2.0 - 1.0) / gl_FragCoord.w;

    OIT(colour, glow, depth);
}
#endif

#endif
