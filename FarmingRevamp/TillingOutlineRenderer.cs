using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Wireframe of voxel boxes still to be worked (furrows to dig, spots to sow), drawn like the knapping recipe outline;
// optional ones (that may be worked, but need not) in grey.
public class TillingOutlineRenderer : IRenderer
{
    readonly ICoreClientAPI api;
    readonly BlockPos pos;
    MeshRef? meshRef;
    readonly Matrixf modelMat = new();
    readonly Vec4f colorMul = new(1, 1, 1, 1);
    readonly Vec3f origin = new(0, 0, 0);

    public double RenderOrder => 0.5;
    public int RenderRange => 24;

    public TillingOutlineRenderer(BlockPos pos, ICoreClientAPI capi)
    {
        this.pos = pos;
        api = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition, "farmingrevamp-tilling");
    }

    // Boxes in grid voxels (x1, y1, z1, x2, y2, z2); a transposed grid is shown with x and z swapped.
    public void RegenMesh(IEnumerable<int[]> boxes, IEnumerable<int[]> optionalBoxes, bool transposed)
    {
        MeshData mesh = new MeshData(24, 36, false, false, true, false);
        mesh.SetMode(EnumDrawMode.Lines);

        int color = api.ColorPreset.GetColor("voxelColorOrange"), optionalColor = ColorUtil.ToRgba(160, 150, 150, 150);

        foreach (var (b, c) in boxes.Select(b => (b, color)).Concat(optionalBoxes.Select(b => (b, optionalColor))))
        {
            // Line cube spans -1..1 on each axis
            MeshData box = LineMeshUtil.GetCube(c);
            for (int i = 0; i < box.xyz.Length; i += 3)
            {
                float across = (b[0] + (box.xyz[i] + 1) / 2 * (b[3] - b[0])) / 16f;
                float along = (b[2] + (box.xyz[i + 2] + 1) / 2 * (b[5] - b[2])) / 16f;
                box.xyz[i] = transposed ? along : across;
                box.xyz[i + 1] = (b[1] + (box.xyz[i + 1] + 1) / 2 * (b[4] - b[1])) / 16f;
                box.xyz[i + 2] = transposed ? across : along;
            }
            mesh.AddMeshData(box);
        }

        meshRef?.Dispose();
        meshRef = mesh.VerticesCount > 0 ? api.Render.UploadMesh(mesh) : null;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (meshRef == null || api.HideGuis) return;

        IRenderAPI rpi = api.Render;
        EntityPos plrPos = api.World.Player.Entity.Pos;
        Vec3d camPos = api.World.Player.Entity.CameraPos;

        colorMul.A = 1 - GameMath.Clamp((float)Math.Sqrt(plrPos.SquareDistanceTo(pos.X, pos.Y, pos.Z)) / 5 - 1f, 0, 1);
        modelMat.Set(rpi.CameraMatrixOriginf).Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);

        float linewidth = api.Settings.Float["wireframethickness"];
        rpi.LineWidth = linewidth;
        rpi.GLEnableDepthTest();
        rpi.GlToggleBlend(true);

        IShaderProgram prog = rpi.GetEngineShader(EnumShaderProgram.Wireframe);
        prog.Use();
        prog.Uniform("origin", origin);
        prog.Uniform("colorIn", colorMul);
        prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
        prog.UniformMatrix("modelViewMatrix", modelMat.Values);
        rpi.RenderMesh(meshRef);
        prog.Stop();

        if (linewidth != 1.6f) rpi.LineWidth = 1.6f;

        // Same Mac workaround as the vanilla knapping renderer
        if (RuntimeEnv.OS != OS.Mac) return;
        rpi.GLDisableDepthTest();
        rpi.GlToggleBlend(false);
    }

    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
        meshRef?.Dispose();
    }
}
