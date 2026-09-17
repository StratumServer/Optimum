// Native port of the game include colorutil.ash (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: colorutil.ash
// optimum-port: verbatim
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_COLORUTIL_GLSL
#define OPTIMUM_INCLUDE_COLORUTIL_GLSL

vec3 rgb2hsv(vec3 c)
{
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 hsv2rgb(vec3 c)
{
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}




float hue2rgb(float f1, float f2, float hue) {
    if (hue < 0.0)
        hue += 1.0;
    else if (hue > 1.0)
        hue -= 1.0;
    float res;
    if ((6.0 * hue) < 1.0)
        res = f1 + (f2 - f1) * 6.0 * hue;
    else if ((2.0 * hue) < 1.0)
        res = f2;
    else if ((3.0 * hue) < 2.0)
        res = f1 + (f2 - f1) * ((2.0 / 3.0) - hue) * 6.0;
    else
        res = f1;
    return res;
}

vec3 hsl2rgb(vec3 hsl) {
    vec3 rgb;

    if (hsl.y == 0.0) {
        rgb = vec3(hsl.z); // Luminance
    } else {
        float f2;

        if (hsl.z < 0.5)
            f2 = hsl.z * (1.0 + hsl.y);
        else
            f2 = hsl.z + hsl.y - hsl.y * hsl.z;

        float f1 = 2.0 * hsl.z - f2;

        rgb.r = hue2rgb(f1, f2, hsl.x + (1.0/3.0));
        rgb.g = hue2rgb(f1, f2, hsl.x);
        rgb.b = hue2rgb(f1, f2, hsl.x - (1.0/3.0));
    }
    return rgb;
}

vec3 hsl2rgb(float h, float s, float l) {
    return hsl2rgb(vec3(h, s, l));
}

vec3 rgb2hsl(vec3 color) {
 	vec3 hsl; // init to 0 to avoid warnings ? (and reverse if + remove first part)

 	float fmin = min(min(color.r, color.g), color.b); //Min. value of RGB
 	float fmax = max(max(color.r, color.g), color.b); //Max. value of RGB
 	float delta = fmax - fmin; //Delta RGB value

 	hsl.z = (fmax + fmin) / 2.0; // Luminance

 	if (delta == 0.0) //This is a gray, no chroma...
 	{
 		hsl.x = 0.0; // Hue
 		hsl.y = 0.0; // Saturation
 	} else //Chromatic data...
 	{
 		if (hsl.z < 0.5)
 			hsl.y = delta / (fmax + fmin); // Saturation
 		else
 			hsl.y = delta / (2.0 - fmax - fmin); // Saturation

 		float deltaR = (((fmax - color.r) / 6.0) + (delta / 2.0)) / delta;
 		float deltaG = (((fmax - color.g) / 6.0) + (delta / 2.0)) / delta;
 		float deltaB = (((fmax - color.b) / 6.0) + (delta / 2.0)) / delta;

 		if (color.r == fmax)
 			hsl.x = deltaB - deltaG; // Hue
 		else if (color.g == fmax)
 			hsl.x = (1.0 / 3.0) + deltaR - deltaB; // Hue
 		else if (color.b == fmax)
 			hsl.x = (2.0 / 3.0) + deltaG - deltaR; // Hue

 		if (hsl.x < 0.0)
 			hsl.x += 1.0; // Hue
 		else if (hsl.x > 1.0)
 			hsl.x -= 1.0; // Hue
 	}

 	return hsl;
 }

#endif
