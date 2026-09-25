// Native port of the game include noise2d.ash (docs/vulkan.md).
// optimum-port-of: noise2d.ash
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_NOISE2D_GLSL
#define OPTIMUM_INCLUDE_NOISE2D_GLSL

// https://github.com/ashima/webgl-noise

vec4 mod2894(vec4 x)
{
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec4 permute4(vec4 x)
{
  return mod2894(((x*34.0)+1.0)*x);
}

vec4 taylorInvSqrt4(vec4 r)
{
  return 1.79284291400159 - 0.85373472095314 * r;
}

vec2 fade2(vec2 t) {
  return t*t*t*(t*(t*6.0-15.0)+10.0);
}

// Classic Perlin noise
float cnoise2(vec2 P)
{
  vec4 Pi = floor(P.xyxy) + vec4(0.0, 0.0, 1.0, 1.0);
  vec4 Pf = fract(P.xyxy) - vec4(0.0, 0.0, 1.0, 1.0);
  Pi = mod2894(Pi); // To avoid truncation effects in permutation
  vec4 ix = Pi.xzxz;
  vec4 iy = Pi.yyww;
  vec4 fx = Pf.xzxz;
  vec4 fy = Pf.yyww;

  vec4 i = permute4(permute4(ix) + iy);

  vec4 gx = fract(i * (1.0 / 41.0)) * 2.0 - 1.0 ;
  vec4 gy = abs(gx) - 0.5 ;
  vec4 tx = floor(gx + 0.5);
  gx = gx - tx;

  vec2 g00 = vec2(gx.x,gy.x);
  vec2 g10 = vec2(gx.y,gy.y);
  vec2 g01 = vec2(gx.z,gy.z);
  vec2 g11 = vec2(gx.w,gy.w);

  vec4 norm = taylorInvSqrt4(vec4(dot(g00, g00), dot(g01, g01), dot(g10, g10), dot(g11, g11)));
  g00 *= norm.x;
  g01 *= norm.y;
  g10 *= norm.z;
  g11 *= norm.w;

  float n00 = dot(g00, vec2(fx.x, fy.x));
  float n10 = dot(g10, vec2(fx.y, fy.y));
  float n01 = dot(g01, vec2(fx.z, fy.z));
  float n11 = dot(g11, vec2(fx.w, fy.w));

  vec2 fade_xy = fade2(Pf.xy);
  vec2 n_x = mix(vec2(n00, n01), vec2(n10, n11), fade_xy.x);
  float n_xy = mix(n_x.x, n_x.y, fade_xy.y);
  return 2.3 * n_xy;
}





// Gradient noise by Inigo Quilez
// https://www.shadertoy.com/view/XdXGW8
vec2 ghash2(vec2 x)
{
    const vec2 k = vec2( 0.3183099, 0.3678794 );
    x = x*k + k.yx;
    return -1.0 + 2.0*fract( 16.0 * k*fract( x.x*x.y*(x.x+x.y)) );
}

float gnoise2( in vec2 p )
{
    vec2 i = floor( p );
    vec2 f = fract( p );

	vec2 u = f*f*(3.0-2.0*f);

    return mix( mix( dot( ghash2( i + vec2(0.0,0.0) ), f - vec2(0.0,0.0) ),
                     dot( ghash2( i + vec2(1.0,0.0) ), f - vec2(1.0,0.0) ), u.x),
                mix( dot( ghash2( i + vec2(0.0,1.0) ), f - vec2(0.0,1.0) ),
                     dot( ghash2( i + vec2(1.0,1.0) ), f - vec2(1.0,1.0) ), u.x), u.y);
}

#endif
