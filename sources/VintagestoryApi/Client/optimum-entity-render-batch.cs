using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

#nullable disable

namespace Vintagestory.API.Client
{
    public interface IOptimumEntityLightSampler
    {
        int OptimumLightSampleCount { get; }

        void GetOptimumLightSampleCoordinates(int sampleIndex, out int x, out int y, out int z);

        void SetOptimumLightSample(int batchId, int sampleIndex, int x, int y, int z, IWorldChunk sourceChunk, float red, float green, float blue, float sunlight);

        void ActivateOptimumLightBatch(int batchId);
    }

    public interface IOptimumEntityShaderRenderer
    {
        bool OptimumShaderStateCompatible { get; }
    }

    public static class OptimumEntityLightConverter
    {
        public static void FromPackedLight(ushort light, int lightSaturation, byte[] hueLevels, byte[] saturationLevels, float[] blockLightLevels, float[] sunlightLevels, out float red, out float green, out float blue, out float sunlight)
        {
            int sunlightLevel = light & 0x1F;
            int blockLightLevel = (light >> 5) & 0x1F;
            int hueLevel = light >> 10;
            int rgb = ColorUtil.HsvToRgb(hueLevels[hueLevel], saturationLevels[lightSaturation], (int)(blockLightLevels[blockLightLevel] * 255f));

            red = (float)(rgb >> 16) / 255f;
            green = (float)((rgb >> 8) & 0xFF) / 255f;
            blue = (float)(rgb & 0xFF) / 255f;
            sunlight = sunlightLevels[sunlightLevel];
        }
    }

    public static class OptimumEntityShaderState
    {
        [ThreadStatic]
        private static IShaderProgram shader;

        [ThreadStatic]
        private static UBORef animationUbo;

        [ThreadStatic]
        private static int useCount;

        public static void Begin(IShaderProgram activeShader, UBORef activeAnimationUbo)
        {
            shader = activeShader;
            animationUbo = activeAnimationUbo;
            useCount = 0;
        }

        public static bool TryGetAnimationUbo(IShaderProgram activeShader, out UBORef activeAnimationUbo)
        {
            UBORef cachedUbo = animationUbo;
            if (cachedUbo != null && !cachedUbo.Disposed && ReferenceEquals(shader, activeShader))
            {
                useCount++;
                activeAnimationUbo = cachedUbo;
                return true;
            }

            activeAnimationUbo = null;
            return false;
        }

        public static int End()
        {
            int uses = useCount;
            shader = null;
            animationUbo = null;
            useCount = 0;
            return uses;
        }
    }
}
