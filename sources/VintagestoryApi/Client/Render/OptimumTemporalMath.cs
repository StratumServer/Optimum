using System;

#nullable disable

namespace Vintagestory.API.Client
{
    /// <summary>
    /// Small, dependency-free conventions shared by Optimum's temporal anti-aliasing
    /// and its upscaler motion-vector adapters. Kept as pure functions so they can be
    /// unit tested without a render context.
    /// </summary>
    public static class OptimumTemporalMath
    {
        /// <summary>
        /// The Halton low-discrepancy sequence, one-indexed (Halton(0, base) is never
        /// requested - jitter sequences start at index 1).
        /// </summary>
        public static double Halton(int index, int radix)
        {
            double result = 0;
            double fraction = 1.0 / radix;
            int i = index;
            while (i > 0)
            {
                result += (i % radix) * fraction;
                i /= radix;
                fraction /= radix;
            }
            return result;
        }

        /// <summary>
        /// Number of distinct jitter offsets in the TAA jitter sequence for a given
        /// render scale: more upscaling needs more sub-pixel samples to converge.
        /// </summary>
        public static int JitterPhaseCount(float renderScale)
        {
            return (int)Math.Ceiling(8.0 * renderScale * renderScale);
        }

        /// <summary>
        /// Applies a sub-pixel projection jitter (in render pixels) to a column-major
        /// perspective matrix produced by Mat4d.Perspective, in place. Matches the
        /// convention used by the TAA jitter pass: P[8]/P[9] are the matrix's x/y
        /// oblique terms, so nudging them shifts every clip-space x/y by a fixed
        /// fraction of clip.w = -z_view, i.e. a constant pixel offset on screen.
        /// </summary>
        public static void ApplyProjectionJitter(double[] projection, double jitterX, double jitterY, double renderWidth, double renderHeight)
        {
            projection[8] -= 2.0 * jitterX / renderWidth;
            projection[9] -= 2.0 * jitterY / renderHeight;
        }

        /// <summary>
        /// The upscalers Optimum drives disagree on the units a stored motion vector
        /// (previousPixel - currentPixel, in render pixels) should be handed over in.
        /// </summary>
        public enum MotionVectorAdapter
        {
            /// <summary>FSR2/FSR3: render pixels, unchanged.</summary>
            Fsr,
            /// <summary>DLSS: normalized by render target size.</summary>
            Dlss,
            /// <summary>XeSS pixel mode: render pixels, unchanged.</summary>
            Xess
        }

        /// <summary>
        /// Rescales a stored motion vector (previousPixel - currentPixel, in render
        /// pixels) into the units the given upscaler adapter expects.
        /// </summary>
        public static (float X, float Y) AdaptMotionVector(float motionPixelsX, float motionPixelsY, int renderWidth, int renderHeight, MotionVectorAdapter adapter)
        {
            switch (adapter)
            {
                case MotionVectorAdapter.Fsr:
                case MotionVectorAdapter.Xess:
                    return (motionPixelsX, motionPixelsY);
                case MotionVectorAdapter.Dlss:
                    return (motionPixelsX / renderWidth, motionPixelsY / renderHeight);
                default:
                    throw new ArgumentOutOfRangeException(nameof(adapter), adapter, null);
            }
        }
    }
}
