using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

#nullable disable

namespace Vintagestory.GameContent;

// Optimum: #9718 fix. Minimal IInFirepitRenderer for the crucible.
// Renders the crucible shape with incandescence glow when heated.
public class CrucibleInFirepitRenderer : IInFirepitRenderer
{
    public double RenderOrder => 0.5;
    public int RenderRange => 20;

    private ICoreClientAPI capi;
    private BlockPos pos;
    private ItemStack stack;
    private MultiTextureMeshRef meshRef;
    private Matrixf modelMat = new Matrixf();
    private float temperature;

    public CrucibleInFirepitRenderer(ICoreClientAPI capi, ItemStack stack, BlockPos pos)
    {
        this.capi = capi;
        this.pos = pos;
        this.stack = stack;

        capi.Tesselator.TesselateBlock(stack.Block, out MeshData mesh);
        meshRef = capi.Render.UploadMultiTextureMesh(mesh);
    }

    public void OnUpdate(float temperature)
    {
        this.temperature = temperature;
    }

    public void OnCookingComplete() { }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (meshRef == null) return;

        IRenderAPI rpi = capi.Render;
        Vec3d camPos = capi.World.Player.Entity.CameraPos;

        rpi.GlDisableCullFace();
        rpi.GlToggleBlend(true);

        IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
        prog.Use();

        int temp = (int)temperature;
        Vec4f lightrgbs = capi.World.BlockAccessor.GetLightRGBs(pos.X, pos.Y, pos.Z);
        float[] glowColor = ColorUtil.GetIncandescenceColorAsColor4f(temp);
        lightrgbs[0] += glowColor[0];
        lightrgbs[1] += glowColor[1];
        lightrgbs[2] += glowColor[2];

        prog.RgbaLightIn = lightrgbs;
        prog.ExtraGlow = (int)GameMath.Clamp((temp - 500) / 4, 0, 255);

        prog.ModelMatrix = modelMat
            .Identity()
            .Translate(pos.X - camPos.X, pos.Y - camPos.Y + 0.0625, pos.Z - camPos.Z)
            .Values;

        prog.ViewMatrix = rpi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

        rpi.RenderMultiTextureMesh(meshRef, "tex");
        prog.Stop();
    }

    public void Dispose()
    {
        meshRef?.Dispose();
    }
}
