#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform mat4 iMvpMatrix;
uniform sampler2D depthTex;
uniform sampler2D cloudMap;
uniform sampler2D cloudCol;
uniform sampler2D liquidDepth;
uniform float cloudMapWidth;
uniform vec3 cloudOffset;
uniform int frame;
uniform float time;
uniform int FrameWidth;
uniform float PerceptionEffectIntensity;

in vec2 uv;
in vec2 ndc;

#include dither.fsh
#include oit.fsh

vec3 hash(vec3 p){

    // https://www.shadertoy.com/view/XlXcW4
    
    // The MIT License
    // Copyright 2017 Inigo Quilez
    // Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

    const uint k = 1103515245U;
    uvec3 x = floatBitsToUint(p);
    x = ((x>>8U)^x.yzx)*k;
    x = ((x>>8U)^x.yzx)*k;
    x = ((x>>8U)^x.yzx)*k;
    return vec3(x)/float(0xffffffffU);

}

float noise(vec3 p){
    vec3 f = smoothstep(0.0, 1.0, fract(p));
    vec3 x = floor(p);
    return mix(mix(mix(hash(x + vec3(0, 0, 0)).x,
                       hash(x + vec3(1, 0, 0)).x, f.x),
                   mix(hash(x + vec3(0, 1, 0)).x,
                       hash(x + vec3(1, 1, 0)).x, f.x), f.y),
               mix(mix(hash(x + vec3(0, 0, 1)).x,
                       hash(x + vec3(1, 0, 1)).x, f.x),
                   mix(hash(x + vec3(0, 1, 1)).x,
                       hash(x + vec3(1, 1, 1)).x, f.x), f.y), f.z);
}

float octave(vec3 p){
    return (noise(p * 2.0) * 0.66 + noise(p * 6.0) * 0.33) * 2.0 - 1.0;
}

mat2 rot(float n){
    return mat2(cos(n), -sin(n), sin(n), cos(n));
}

vec3 warp(vec3 d, float f){
    if(f < 0.0001) return d;
    d.xz *= rot(octave(d * 2.0 + time * 0.05) * f);
    d.xy *= rot(octave(d * 1.5 + time * 0.04) * f);
    d.zy *= rot(octave(d * 1.5 - time * 0.04) * f);
    return normalize(d);
}

vec3 curve(vec3 d, float f){
    d.xy *= rot(d.x * f);
    d.zy *= rot(d.z * f);
    return normalize(d);
}

vec3 unproject(vec4 x){
    return x.xyz / max(x.w, 0.0001);
}

float volume(float o, float d, vec2 m, float t, float f){
    m = (m - o) / d;
    return 1.0 - exp(-max(0.0, min(max(m.x, m.y), t) - max(0.0, min(m.x, m.y))) * f);
}

vec2 intersect(float o, float d, vec2 m){
    m = (m - o) / d;
    float near = min(m.x, m.y);
    float far = max(m.x, m.y);
    if(near > far || far < 0.0) return vec2(-1.0);
    return vec2(max(0.0, near), max(0.0, far - max(0.0, near)));
}

float halfsmooth(float x, float t){
    return x > t ? (x - t / 2.0) : (x * x * x * (1.0 - x * 0.5 / t) / t / t);
}

vec4 traverse(vec3 o, vec3 d, float far, float T){

    ivec2 p = ivec2(floor(o.xz));
    ivec2 istep = ivec2(sign(d.xz));
    vec2 tdelta, tmax;
    tdelta.x = 1.0 / max(0.0001, abs(d.x));
    tdelta.y = 1.0 / max(0.0001, abs(d.z));
    tmax.x = (d.x > 0.0 ? floor(o.x) + 1.0 - o.x : o.x - floor(o.x)) * tdelta.x;
    tmax.y = (d.z > 0.0 ? floor(o.z) + 1.0 - o.z : o.z - floor(o.z)) * tdelta.y;
    float t = 0.0;
    vec4 k = vec4(0.0);

    // Optimum: cap march steps at 100 (vanilla uses 200).
    for(int i = 0; i < 100; i++){

        vec4 map = texelFetch(cloudMap, p, 0);

        if(map.r > 0.0){

            vec4 col = texelFetch(cloudCol, p, 0);

            float v = volume(
                o.y + d.y * t,
                d.y,
                map.ba,
                min(far, min(tmax.x, tmax.y)) - t,
                map.r
            );

            if(v > 0.0){

                k += (1.0 - k.a) * col * v;

                float bin = log(halfsmooth((T + t) * 50.0, 500.0) / OIT_BIN_SCALE + 1.0);
                for(int i = 0; i < OIT_BINS; i++){
                    float b = OITbellcurve(bin - float(i));
                    if(i == (OIT_BINS-1) && bin > float(OIT_BINS-1)) b = 1.0;
                    OITreveal[i] *= 1.0 - col.a * v * b;
                }

            }

            if(k.a > 0.99) break;

        }

        if(tmax.x < tmax.y){
            p.x += istep.x;
            t = tmax.x;
            tmax.x += tdelta.x;
        }else{
            p.y += istep.y;
            t = tmax.y;
            tmax.y += tdelta.y;
        }

        if(t > far) break;

    }

    return k;

}

void main(){

    const float cloudTileSize = 50.0;

    vec3 origin = unproject(iMvpMatrix * vec4(ndc, -1.0, 1.0));
    vec3 direction = normalize(unproject(iMvpMatrix * vec4(ndc, 1.0, 1.0)) - origin);
    vec3 world = unproject(iMvpMatrix * vec4(ndc, texelFetch(depthTex, ivec2(gl_FragCoord), 0).r * 2.0 - 1.0, 1.0));
    vec3 liquid = unproject(iMvpMatrix * vec4(ndc, texelFetch(liquidDepth, ivec2(gl_FragCoord / 4.0), 0).r * 2.0 - 1.0, 1.0));

    float far = min(
        distance(origin, world),
        distance(origin, liquid)
    );

    direction = curve(direction, 0.07);
    direction = warp(direction, PerceptionEffectIntensity * 0.03);

    float height = max(0.0, cloudOffset.y - origin.y);

    origin.y -= cloudOffset.y;

    vec2 plane = intersect(
        origin.y,
        direction.y,
        vec2(-12.5 - 500 * 0.1, 12.5 + 500.0)
    );

    float near = plane.x;

    if(near < 0.0 || far < near) discard;

    origin += direction * near;
    origin.xz -= cloudOffset.xz;
    origin /= cloudTileSize;
    origin.xz += cloudMapWidth / 2.0;

    far -= near;
    far = min(far, plane.y);
    far = min(far, cloudMapWidth * cloudTileSize / 2.0 - near);
    far /= cloudTileSize;

    outGlow = OITaccumulation0 = OITaccumulation1 = OITaccumulation2 = vec4(0.0);
    OITreveal = outReveal = vec4(1.0);

    vec4 k = traverse(origin, direction, far, plane.x / cloudTileSize);

    if(k.a <= 0.0) discard;

    float s = FrameWidth / 240.0 / 11.0;
    float n = NoiseFromPixelPosition(ivec2(gl_FragCoord.xy), frame + 256, FrameWidth).r * s;

    k = exp(log(k + 1.0) + n) - 1.0;

    if(k.a <= 0.0) discard;

    for(int i = 0; i < OIT_BINS; i++)
        OITaccumulate(i, k * (1.0 - OITreveal[i]));

    outReveal = vec4(1.0 - k.a);
    outGlow.a = k.a;
}
