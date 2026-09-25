// Native port of the game include noise3d.ash (docs/vulkan.md).
// optimum-port-of: noise3d.ash
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_NOISE3D_GLSL
#define OPTIMUM_INCLUDE_NOISE3D_GLSL

float cmod289(float x){return x - floor(x * (1.0 / 289.0)) * 289.0;}
vec4 cmod289(vec4 x){return x - floor(x * (1.0 / 289.0)) * 289.0;}
vec4 perm(vec4 x){return cmod289(((x * 34.0) + 1.0) * x);}

float valuenoise(vec3 p){
    vec3 a = floor(p);
    vec3 d = p - a;
    d = d * d * (3.0 - 2.0 * d);

    vec4 b = a.xxyy + vec4(0.0, 1.0, 0.0, 1.0);
    vec4 k1 = perm(b.xyxy);
    vec4 k2 = perm(k1.xyxy + b.zzww);

    vec4 c = k2 + a.zzzz;
    vec4 k3 = perm(c);
    vec4 k4 = perm(c + 1.0);

    vec4 o1 = fract(k3 * (1.0 / 41.0));
    vec4 o2 = fract(k4 * (1.0 / 41.0));

    vec4 o3 = o2 * d.z + o1 * (1.0 - d.z);
    vec2 o4 = o3.yw * d.x + o3.xz * (1.0 - d.x);

    return o4.y * d.y + o4.x * (1.0 - d.y);
}





// https://github.com/ashima/webgl-noise

vec3 mod289(vec3 x)
{
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec4 mod289(vec4 x)
{
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec4 permute(vec4 x)
{
  return mod289(((x*34.0)+1.0)*x);
}

vec4 taylorInvSqrt(vec4 r)
{
  return 1.79284291400159 - 0.85373472095314 * r;
}

vec3 fade(vec3 t) {
  return t*t*t*(t*(t*6.0-15.0)+10.0);
}

// Classic Perlin noise
float cnoise(vec3 P)
{
  vec3 Pi0 = floor(P); // Integer part for indexing
  vec3 Pi1 = Pi0 + vec3(1.0); // Integer part + 1
  Pi0 = mod289(Pi0);
  Pi1 = mod289(Pi1);
  vec3 Pf0 = fract(P); // Fractional part for interpolation
  vec3 Pf1 = Pf0 - vec3(1.0); // Fractional part - 1.0
  vec4 ix = vec4(Pi0.x, Pi1.x, Pi0.x, Pi1.x);
  vec4 iy = vec4(Pi0.yy, Pi1.yy);
  vec4 iz0 = Pi0.zzzz;
  vec4 iz1 = Pi1.zzzz;

  vec4 ixy = permute(permute(ix) + iy);
  vec4 ixy0 = permute(ixy + iz0);
  vec4 ixy1 = permute(ixy + iz1);

  vec4 gx0 = ixy0 * (1.0 / 7.0);
  vec4 gy0 = fract(floor(gx0) * (1.0 / 7.0)) - 0.5;
  gx0 = fract(gx0);
  vec4 gz0 = vec4(0.5) - abs(gx0) - abs(gy0);
  vec4 sz0 = step(gz0, vec4(0.0));
  gx0 -= sz0 * (step(0.0, gx0) - 0.5);
  gy0 -= sz0 * (step(0.0, gy0) - 0.5);

  vec4 gx1 = ixy1 * (1.0 / 7.0);
  vec4 gy1 = fract(floor(gx1) * (1.0 / 7.0)) - 0.5;
  gx1 = fract(gx1);
  vec4 gz1 = vec4(0.5) - abs(gx1) - abs(gy1);
  vec4 sz1 = step(gz1, vec4(0.0));
  gx1 -= sz1 * (step(0.0, gx1) - 0.5);
  gy1 -= sz1 * (step(0.0, gy1) - 0.5);

  vec3 g000 = vec3(gx0.x,gy0.x,gz0.x);
  vec3 g100 = vec3(gx0.y,gy0.y,gz0.y);
  vec3 g010 = vec3(gx0.z,gy0.z,gz0.z);
  vec3 g110 = vec3(gx0.w,gy0.w,gz0.w);
  vec3 g001 = vec3(gx1.x,gy1.x,gz1.x);
  vec3 g101 = vec3(gx1.y,gy1.y,gz1.y);
  vec3 g011 = vec3(gx1.z,gy1.z,gz1.z);
  vec3 g111 = vec3(gx1.w,gy1.w,gz1.w);

  vec4 norm0 = taylorInvSqrt(vec4(dot(g000, g000), dot(g010, g010), dot(g100, g100), dot(g110, g110)));
  g000 *= norm0.x;
  g010 *= norm0.y;
  g100 *= norm0.z;
  g110 *= norm0.w;
  vec4 norm1 = taylorInvSqrt(vec4(dot(g001, g001), dot(g011, g011), dot(g101, g101), dot(g111, g111)));
  g001 *= norm1.x;
  g011 *= norm1.y;
  g101 *= norm1.z;
  g111 *= norm1.w;

  float n000 = dot(g000, Pf0);
  float n100 = dot(g100, vec3(Pf1.x, Pf0.yz));
  float n010 = dot(g010, vec3(Pf0.x, Pf1.y, Pf0.z));
  float n110 = dot(g110, vec3(Pf1.xy, Pf0.z));
  float n001 = dot(g001, vec3(Pf0.xy, Pf1.z));
  float n101 = dot(g101, vec3(Pf1.x, Pf0.y, Pf1.z));
  float n011 = dot(g011, vec3(Pf0.x, Pf1.yz));
  float n111 = dot(g111, Pf1);

  vec3 fade_xyz = fade(Pf0);
  vec4 n_z = mix(vec4(n000, n100, n010, n110), vec4(n001, n101, n011, n111), fade_xyz.z);
  vec2 n_yz = mix(n_z.xy, n_z.zw, fade_xyz.y);
  float n_xyz = mix(n_yz.x, n_yz.y, fade_xyz.x);
  return 2.2 * n_xyz;
}


// Value noise by https://www.shadertoy.com/view/4sfGzS; 30.6.21 radfast updated with part of the mod289 approach from https://gist.github.com/patriciogonzalezvivo/670c22f3966e662d2f83
const vec3 vn1 = vec3(0.0,0.0,1.0);
const vec3 vn2 = vec3(0.0,1.0,0.0);
const vec3 vn3 = vec3(0.0,1.0,1.0);
const vec3 vn4 = vec3(1.0,0.0,0.0);
const vec3 vn5 = vec3(1.0,0.0,1.0);
const vec3 vn6 = vec3(1.0,1.0,0.0);
const vec3 vn7 = vec3(1.0,1.0,1.0);



vec3 ghash( vec3 p )
{
	vec3 o;
	// these constants are the matrix m
	// individual components multiplied, because the whole matrix multiplication produces float rounding differences from C# equivalent code (Bell pepper)
	o.x = 127.1 * p.x + 311.7 * p.y + 74.7 * p.z;
	o.y = 269.5 * p.x + 183.3 * p.y + 246.1 * p.z;
	o.z = 113.5 * p.x + 271.9 * p.y + 124.6 * p.z;
	vec3 q = ((o * 0.025) + 8.0) * o;   // the constants 4.25 and 8.0 found empirically to give similar noise distribution to the sin approach
	return -1.0 + 2.0*fract(mod(q, 289.0) * (1.0 / 41.0));
}


float gnoise( in vec3 p )
{
    vec3 i = floor( p );
    vec3 f = p - i;

    vec3 u = f*f*(3.0-2.0*f);

    vec4 a = vec4 ( dot(ghash(i), f),
                    dot(ghash(i + vn1), f - vn1),
    		    dot(ghash(i + vn2), f - vn2),
                    dot(ghash(i + vn3), f - vn3));
    vec4 b = vec4 ( dot(ghash(i + vn4), f - vn4),
                    dot(ghash(i + vn5), f - vn5),
    		    dot(ghash(i + vn6), f - vn6),
                    dot(ghash(i + vn7), f - vn7));

    vec4 c = mix(a, b, u.x);
    vec2 rg = mix(c.xy, c.zw, u.y);

    // Added 1.2 here because our old noise was stronger
    return 1.2 * mix(rg.x, rg.y, u.z);
}





// Gradient noise by Inigo Quilez
// https://www.shadertoy.com/view/XdXGW8
vec2 ghash(vec2 x)
{
    const vec2 k = vec2( 0.3183099, 0.3678794 );
    x = x*k + k.yx;
    return -1.0 + 2.0*fract( 16.0 * k*fract( x.x*x.y*(x.x+x.y)) );
}

float gnoise( in vec2 p )
{
    vec2 i = floor( p );
    vec2 f = fract( p );

	vec2 u = f*f*(3.0-2.0*f);

    return mix( mix( dot( ghash( i + vec2(0.0,0.0) ), f - vec2(0.0,0.0) ),
                     dot( ghash( i + vec2(1.0,0.0) ), f - vec2(1.0,0.0) ), u.x),
                mix( dot( ghash( i + vec2(0.0,1.0) ), f - vec2(0.0,1.0) ),
                     dot( ghash( i + vec2(1.0,1.0) ), f - vec2(1.0,1.0) ), u.x), u.y);
}

#endif
