using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

#nullable disable

namespace Vintagestory.GameContent
{
    // Optimum: fix #9718 crucible heat-glow in firepit.
    // Renders the crucible block mesh with temperature-based incandescence glow.
    public class CrucibleInFirepitRenderer : IInFirepitRenderer
    {
        public double RenderOrder => 0.5;
        public int RenderRange => 20;

        ICoreClientAPI capi;
        MultiTextureMeshRef meshRef;
        BlockPos pos;
        float temp;
        readonly int tempGlowMode;
        Matrixf ModelMat = new Matrixf();
        Vec4f glowRgb = new Vec4f();
        Vec4f avgColorVec = new Vec4f(1f, 1f, 1f, 1f);

        public CrucibleInFirepitRenderer(ICoreClientAPI capi, Block crucibleBlock, BlockPos pos)
        {
            this.capi = capi;
            this.pos = pos;
            tempGlowMode = crucibleBlock?.Attributes?["tempGlowMode"].AsInt(0) ?? 0;

            BakedCompositeTexture baked = crucibleBlock?.FirstTextureInventory?.Baked;
            if (baked != null && capi.BlockTextureAtlas != null)
            {
                ColorUtil.ToRGBAVec4f(capi.BlockTextureAtlas.GetAverageColor(baked.TextureSubId), ref avgColorVec);
            }

            capi.Tesselator.TesselateBlock(crucibleBlock, out MeshData mesh);
            meshRef = capi.Render.UploadMultiTextureMesh(mesh);
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (meshRef == null) return;

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);

            int itemp = temp > 500f ? (int)GameMath.Min(temp, 10000f) : 0;
            Vec4f lightrgbs = capi.World.BlockAccessor.GetLightRGBs(pos.X, pos.Y, pos.Z);
            ColorUtil.GetIncandescenceColorFloats(itemp, out float gr, out float gg, out float gb);
            glowRgb.R = gr;
            glowRgb.G = gg;
            glowRgb.B = gb;
            int extraGlow = GameMath.Clamp((itemp - 500) / 3, 0, 255);
            glowRgb.A = extraGlow / 255f;

            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);

            prog.DontWarpVertices = 0;
            prog.AddRenderFlags = 0;
            prog.RgbaAmbientIn = rpi.AmbientColor;
            prog.RgbaFogIn = rpi.FogColor;
            prog.FogMinIn = rpi.FogMin;
            prog.FogDensityIn = rpi.FogDensity;
            prog.RgbaTint = ColorUtil.WhiteArgbVec;
            prog.NormalShaded = 1;
            prog.ExtraGodray = 0;
            prog.SsaoAttn = 0;
            prog.AlphaTest = 0.05f;
            prog.OverlayOpacity = 0;

            prog.TempGlowMode = extraGlow > 0 ? tempGlowMode : 0;
            prog.AverageColor = avgColorVec;
            prog.RgbaLightIn = lightrgbs;
            prog.RgbaGlowIn = glowRgb;
            prog.ExtraGlow = extraGlow;

            prog.ModelMatrix = ModelMat
                .Identity()
                .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z)
                .Values
            ;

            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            rpi.RenderMultiTextureMesh(meshRef, "tex");

            prog.TempGlowMode = 0;
            prog.ExtraGlow = 0;
            prog.Stop();
        }

        public void OnUpdate(float temperature)
        {
            temp = temperature;
        }

        public void OnCookingComplete() { }

        public void Dispose()
        {
            meshRef?.Dispose();
        }
    }
}
