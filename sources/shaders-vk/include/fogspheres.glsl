// Native port of the game include fogspheres.ash (docs/vulkan-native-shaders.md section 1).
// optimum-port-of: fogspheres.ash
// optimum-port: verbatim
// optimum-program-uniform: float fogSpheres[3 * 8]
// optimum-program-uniform: int fogSphereQuantity
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_FOGSPHERES_GLSL
#define OPTIMUM_INCLUDE_FOGSPHERES_GLSL

// const int MaxSpheres = 3;
/// Each sphere has 8 floats:
/// 3 floats x/y/z offset to the player
/// 1 float radius
/// 1 float density
/// 3 floats rgb color


float getSpheresFogAmount(vec3 worldPos) {
	if (fogSphereQuantity == 0) return 0.0;

	float depth = length(worldPos);
	float fogamount = 0;

	for (int i = 0; i < fogSphereQuantity; i++) {
		vec3 L = vec3(fogSpheres[i * 8], fogSpheres[i * 8 +1], fogSpheres[i * 8 + 2]);
		float radius = fogSpheres[i * 8 + 3];
		float density = fogSpheres[i * 8 + 4];

		// https://www.scratchapixel.com/lessons/3d-basic-rendering/minimal-ray-tracer-rendering-simple-shapes/ray-sphere-intersection.html
		// Geometric solution
		vec3 D = normalize(worldPos.xyz);

		// Are we inside the sphere?
		float outsideness = length(L) / radius;
		float tca = dot(L, D);

		// We are inside the sphere looking
		if (outsideness < 1) {
			float thc=0;
			if (tca >= 0)  {
				thc = sqrt(radius*radius - dot(L,L) + tca*tca);
			} else {
				thc = sqrt(radius*radius - dot(L,L));
			}

			thc = min(thc, depth);
			fogamount += thc * density;
		} else {

			if (tca >= 0) {
				float d2 = dot(L, L) - tca * tca;
				if (d2 < radius * radius) {
					float thc = sqrt(radius * radius - d2);
					float t0 = tca - thc;
					float t1 = tca + thc;

					float tf = depth; // t of the vertex. Might be inside our sphere

					// So either use the exit point of the sphere (t1), or vertex depth, whichever is nearer
					t1 = min(tf, t1);

					if (tf > t0) {
						fogamount += (t1 - t0) * density;
					}
				}
			}
		}
	}

	return fogamount;
}


vec4 applySpheresFog(vec4 color, float standardFogAmount, vec3 worldPos) {
	if (fogSphereQuantity == 0) return color;

	float depth = length(worldPos);

	for (int i = 0; i < fogSphereQuantity; i++) {
		vec3 L = vec3(fogSpheres[i * 8], fogSpheres[i * 8 +1], fogSpheres[i * 8 + 2]);
		float radius = fogSpheres[i * 8 + 3];
		float density = fogSpheres[i * 8 + 4];
		vec3 fogrgb = vec3(fogSpheres[i * 8 + 5], fogSpheres[i * 8 + 6], fogSpheres[i * 8 + 7]);

		float fogamount = 0;

		// https://www.scratchapixel.com/lessons/3d-basic-rendering/minimal-ray-tracer-rendering-simple-shapes/ray-sphere-intersection.html
		// Geometric solution
		vec3 D = normalize(worldPos.xyz);

		// Are we inside the sphere?
		float outsideness = length(L) / radius;
		float tca = dot(L, D);

		// We are inside the sphere looking
		if (outsideness < 1) {
			float thc=0;
			if (tca >= 0)  {
				thc = sqrt(radius*radius - dot(L,L) + tca*tca);
			} else {
				thc = sqrt(radius*radius - dot(L,L));
			}

			thc = min(thc, depth);
			fogamount = thc * density;
		} else {

			if (tca >= 0) {
				float d2 = dot(L, L) - tca * tca;
				if (d2 < radius * radius) {
					float thc = sqrt(radius * radius - d2);
					float t0 = tca - thc;
					float t1 = tca + thc;

					float tf = depth; // t of the vertex. Might be inside our sphere

					// So either use the exit point of the sphere (t1), or vertex depth, whichever is nearer
					t1 = min(tf, t1);

					if (tf > t0) {
						fogamount = (t1 - t0) * density;
					}
				}
			}
		}

		color.rgb = mix(color.rgb, fogrgb, clamp(fogamount - (standardFogAmount - fogamount), 0, 1));
	}


	return color;
}

#endif
