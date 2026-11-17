#version 330 core

uniform sampler2D accumulation;
uniform sampler2D revealage;
uniform sampler2D inGlow;

uniform sampler2D OITreveal;
uniform sampler2DArray OITaccumulation;

in vec2 v_texcoord;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
#if !defined(SSAO_DEPTH_RECONSTRUCT) || SSAO_DEPTH_RECONSTRUCT == 0
layout(location = 3) out vec4 outGPosition;
#endif
#endif

#define OIT_BINS 3

vec3 unproject(vec4 a){
    return a.w < 0.0001 ? vec3(0.0) : a.xyz / a.w;
}

void main(){

    vec4 reveal = 1.0 - texelFetch(OITreveal, ivec2(gl_FragCoord), 0);
    float anet = 1.0 - texelFetch(revealage, ivec2(gl_FragCoord), 0).r;
    vec4 k = vec4(0.0);
    float a = 1.0;

    for(int i = 0; i < OIT_BINS; i++){

        vec4 bin = texelFetch(OITaccumulation, ivec3(gl_FragCoord.xy, i), 0);
        float anet_k = reveal[i];

        k += vec4(unproject(bin) * anet_k, anet_k) * a;
        a *= 1.0 - anet_k;

    }

    outColor = vec4(unproject(k), anet);
    outGlow = texture(inGlow, v_texcoord);

}
