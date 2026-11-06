using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Draws a TillingGrid heightmap as 1×1-voxel columns cut from a block's plain cube mesh.
public static class SoilColumnMesh
{
    // Adds the columns for which `include` holds (all if null), cut from `block`; a transposed grid is shown with x and z swapped.
    public static void Add(ITerrainMeshPool mesher, ITesselatorAPI tesselator, Block block, TillingGrid grid, bool transposed, System.Func<int, int, bool>? include = null)
    {
        tesselator.TesselateBlock(block, out MeshData cube);
        AddColumns(mesher, cube, grid, transposed, include, null);
    }

    // Adds the grass (or forest litter) of a topsoil block over the columns for which `include` holds. Vanilla draws it
    // with the topsoil shader from the block's second texture, which block entity meshes cannot use, so it is drawn here
    // as its own faces, tinted by climate and season: the right half of that texture on top, the left half on the sides.
    public static void AddTopsoilOverlay(ICoreClientAPI capi, ITerrainMeshPool mesher, ITesselatorAPI tesselator, Block block, BlockPos pos, TillingGrid grid, System.Func<int, int, bool> include)
    {
        TextureAtlasPosition? overlay = tesselator.GetTextureSource(block, 0, true)["specialSecondTexture"];
        if (overlay == null) return;

        tesselator.TesselateBlock(block, out MeshData cube);
        float half = (overlay.x2 - overlay.x1) / 2;
        for (int f = 0; f < cube.XyzFacesCount; f++)
        {
            int facing = cube.XyzFaces[f] - 1;
            int v0 = f * cube.VerticesPerFace, v1 = v0 + cube.VerticesPerFace;
            if (facing == BlockFacing.DOWN.Index)
            {
                // Collapsed: nothing to draw underneath
                for (int v = v0; v < v1; v++) Array.Copy(cube.xyz, 3 * v0, cube.xyz, 3 * v, 3);
                continue;
            }

            // The face's uv rectangle (on its own texture) mapped onto the overlay's half for that face
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            for (int v = v0; v < v1; v++)
            {
                uMin = Math.Min(uMin, cube.Uv[2 * v]); uMax = Math.Max(uMax, cube.Uv[2 * v]);
                vMin = Math.Min(vMin, cube.Uv[2 * v + 1]); vMax = Math.Max(vMax, cube.Uv[2 * v + 1]);
            }
            float uStart = overlay.x1 + (facing == BlockFacing.UP.Index ? half : 0);
            for (int v = v0; v < v1; v++)
            {
                cube.Uv[2 * v] = uStart + (cube.Uv[2 * v] - uMin) / (uMax - uMin) * half;
                cube.Uv[2 * v + 1] = overlay.y1 + (cube.Uv[2 * v + 1] - vMin) / (vMax - vMin) * (overlay.y2 - overlay.y1);
            }
        }

        cube.TextureIds = new[] { overlay.atlasTextureId };
        Array.Fill(cube.TextureIndices, (byte)0);
        Array.Fill(cube.RenderPassesAndExtraBits, (short)EnumChunkRenderPass.Opaque);
        Array.Fill(cube.ClimateColorMapIds, (byte)(block.ClimateColorMapResolved == null ? 0 : block.ClimateColorMapResolved.RectIndex + 1));
        Array.Fill(cube.SeasonColorMapIds, (byte)(block.SeasonColorMapResolved == null ? 0 : block.SeasonColorMapResolved.RectIndex + 1));
        // Just outside the soil faces, so the two do not flicker
        cube.Scale(new Vec3f(0.5f, 0.5f, 0.5f), 1.002f, 1.002f, 1.002f);

        AddColumns(mesher, cube, grid, false, include, capi.World.GetColorMapData(block, pos.X, pos.Y, pos.Z));
    }

    static void AddColumns(ITerrainMeshPool mesher, MeshData cube, TillingGrid grid, bool transposed, System.Func<int, int, bool>? include, ColorMapData? colorMap)
    {
        for (int x = 0; x < TillingGrid.Size; x++)
        {
            for (int z = 0; z < TillingGrid.Size; z++)
            {
                if (include != null && !include(x, z)) continue;
                MeshData cell = cube.Clone().Scale(new Vec3f(), 1 / 16f, grid[x, z] / 16f, 1 / 16f).Translate((transposed ? z : x) / 16f, 0, (transposed ? x : z) / 16f);
                CropUv(cube, cell);
                if (colorMap is ColorMapData map) mesher.AddMeshData(cell, map); else mesher.AddMeshData(cell);
            }
        }
    }

    // Gives a cell the part of each face texture at its position in the block, so the soil looks like the
    // untouched block instead of squeezing the whole texture onto every column. Each quad's uv is linear in the
    // unit-cube corner positions; that mapping is solved from the cube and applied to the cell's vertices.
    static void CropUv(MeshData cube, MeshData cell)
    {
        float[] slope = new float[3];
        for (int q = 0; q + 4 <= cube.VerticesCount; q += 4)
        {
            for (int c = 0; c < 2; c++)
            {
                Array.Clear(slope);
                for (int a = q; a < q + 4; a++)
                {
                    for (int b = q; b < q + 4; b++)
                    {
                        int axis = -1, diffs = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            if (cube.xyz[3 * a + k] != cube.xyz[3 * b + k]) { axis = k; diffs++; }
                        }
                        if (diffs == 1) slope[axis] = (cube.Uv[2 * b + c] - cube.Uv[2 * a + c]) / (cube.xyz[3 * b + axis] - cube.xyz[3 * a + axis]);
                    }
                }

                float offset = cube.Uv[2 * q + c];
                for (int k = 0; k < 3; k++) offset -= slope[k] * cube.xyz[3 * q + k];

                for (int i = q; i < q + 4; i++)
                {
                    float uv = offset;
                    // Clamped: heaps above the block reuse the top edge of the texture
                    for (int k = 0; k < 3; k++) uv += slope[k] * Math.Clamp(cell.xyz[3 * i + k], 0f, 1f);
                    cell.Uv[2 * i + c] = uv;
                }
            }
        }
    }
}
