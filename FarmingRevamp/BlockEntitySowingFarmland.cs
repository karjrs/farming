using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FarmingRevamp;

// Farmland being sown: right clicks with seeds place a seed in each of the 9 highlighted spots in the furrows, then
// the highlighted ridges are collapsed into the furrows next to them, covering the seeds: by hoe (a ridge per hold, or
// painted), seed covering tools like the shovel and rake (a band across per hold) or bare hand (a voxel per click). Once covered it turns back
// into farmland (covered furrows) with the crop planted and the original farmland's nutrients and moisture.
public class BlockEntitySowingFarmland : BlockEntityFurrowWork, ITexPositionSource
{
    const int PlaceSeedPacket = 1002;
    // Three 2-voxel spots along each furrow, starting at these z
    static readonly int[] SpotZ = { 3, 7, 11 };
    static readonly int SpotCount = TillingGrid.Furrows.Length * SpotZ.Length;
    // Each sown seed is two voxels lying diagonally in its 2×2 spot (across, along)
    static readonly (int across, int along)[] SeedVoxels = { (0, 0), (1, 1) };

    bool[] seeded = new bool[SpotCount];
    AssetLocation? seedCode;
    // The farmland block entity's data, restored when the crop is planted
    ITreeAttribute? farmlandData;

    // Client side
    // One unit voxel per SeedVoxels entry, each coloured by one pixel of the seed's texture
    MeshData[]? seedVoxelMeshes;
    TextureAtlasPosition? seedTexPos;

    public BlockEntitySowingFarmland()
    {
        Grid = TillingGrid.Tilled();
    }

    // Server side, right after the farmland was turned into this block
    public void Setup(AssetLocation seed, ITreeAttribute farmland)
    {
        seedCode = seed;
        farmlandData = farmland;
        MarkDirty(true);
    }

    protected override Block? ColumnBlock => Block;

    static (int[] cols, int z) Spot(int i) => (TillingGrid.Furrows[i / SpotZ.Length], SpotZ[i % SpotZ.Length]);

    bool AllSeeded => seeded.All(s => s);

    protected override int[][] Lines => TillingGrid.Ridges;

    // While sowing boxes 0..8 are the seed spots (index = spot), once sown boxes 0..5 are the ridges (index = ridge);
    // the rest of the furrows and the field only make it selectable.
    protected override Cuboidf[] BuildSelectionBoxes()
    {
        var boxes = new List<Cuboidf>();
        if (AllSeeded)
        {
            foreach (int[] cols in TillingGrid.Ridges.Concat(TillingGrid.Furrows))
            {
                boxes.Add(Box(cols[0], cols[^1] + 1, TillingGrid.FurrowStartZ, TillingGrid.FurrowEndZ));
            }
            AddFieldBoxes(boxes, x => TillingGrid.IsFurrow(x, TillingGrid.FurrowStartZ) || TillingGrid.Ridges.Any(r => r[0] == x));
            return boxes.ToArray();
        }

        for (int i = 0; i < SpotCount; i++)
        {
            var (cols, z) = Spot(i);
            boxes.Add(Box(cols[0], cols[^1] + 1, z, z + 2));
        }
        foreach (int[] cols in TillingGrid.Furrows)
        {
            int z = TillingGrid.FurrowStartZ;
            foreach (int spotZ in SpotZ.Append(TillingGrid.FurrowEndZ))
            {
                if (spotZ > z) boxes.Add(Box(cols[0], cols[^1] + 1, z, spotZ));
                z = spotZ + 2;
            }
        }
        AddFieldBoxes(boxes, x => TillingGrid.IsFurrow(x, TillingGrid.FurrowStartZ));
        return boxes.ToArray();
    }

    // Seed spots still to sow, then one outline per ridge voxel still to collapse (the heaped voxel above the surface)
    protected override IEnumerable<int[]> OutlineBoxes()
    {
        if (AllSeeded)
        {
            foreach (int[] box in LineVoxelBoxes(TillingGrid.Surface)) yield return box;
            yield break;
        }

        for (int i = 0; i < SpotCount; i++)
        {
            if (seeded[i]) continue;
            var (cols, z) = Spot(i);
            yield return new[] { cols[0], TillingGrid.FurrowHeight, z, cols[^1] + 1, TillingGrid.Surface, z + 2 };
        }
    }

    // Client side
    public bool CanSeed(int selectionBoxIndex) => selectionBoxIndex >= 0 && selectionBoxIndex < SpotCount && !seeded[selectionBoxIndex];

    public void SendPlaceSeed(int selectionBoxIndex) => SendPacket(PlaceSeedPacket, new[] { (byte)selectionBoxIndex });

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(player, packetid, data);
        if (packetid != PlaceSeedPacket || data.Length != 1 || !CanSeed(data[0])) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.BuildOrBreak)) return;
        if (player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible is not ItemSowingSeed) return;

        seeded[data[0]] = true;
        Api.World.PlaySoundAt(new AssetLocation("sounds/block/plant"), Pos, 0, null, true, 16, 0.5f);
        Refresh();
        MarkDirty(true);
    }

    protected override bool CanWork(int ridge, int z) => Grid.CanCollapse(ridge, z);

    protected override bool Workable => AllSeeded;

    // Hoe one ridge, seed covering tools their band, bare hand single voxels
    public override int WorkWidth(ItemStack? tool) =>
        tool == null ? 1 : base.WorkWidth(tool);

    protected override bool HoeStep(int row, int z) => Grid.CollapseStep(row, z);

    protected override bool IsComplete => Grid.IsAllFilled;

    protected override void OnComplete(IPlayer player)
    {
        Item? seed = seedCode == null ? null : Api.World.GetItem(seedCode);
        Block? farmland = Api.World.GetBlock(new AssetLocation("farmingrevamp", $"farmland-{Block.Variant["state"]}-{Block.Variant["fertility"]}-{Block.Variant["side"]}-covered"));
        Block? crop = seed == null ? null : Api.World.GetBlock(seed.CodeWithPath("crop-" + seed.Variant["type"] + "-1"));
        if (farmland == null || seed == null || crop == null) return;

        Api.World.BlockAccessor.SetBlock(farmland.BlockId, Pos);
        if (Api.World.BlockAccessor.GetBlockEntity(Pos) is not BlockEntityFarmland be) return;
        if (farmlandData != null) be.FromTreeAttributes(farmlandData, Api.World);
        be.TryPlant(crop, new DummySlot(new ItemStack(seed)), player.Entity, new BlockSelection { Position = Pos.Copy(), Face = BlockFacing.UP });
        be.MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("sounds/block/plant"), Pos, 0.4375);
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        GenSeedMesh();
    }

    // Seed voxels: cubes with the seed item's texture (added to the block atlas, like vanilla's fertilizer overlay)
    // whose uvs all point at one opaque pixel of it, so each voxel is a solid seed colour
    void GenSeedMesh()
    {
        if (Api is not ICoreClientAPI capi || seedVoxelMeshes != null || seedCode == null) return;
        if (Api.World.GetItem(seedCode) is not Item seed || !seed.Textures.TryGetValue("seed", out CompositeTexture? tex)) return;

        IBitmap? bmp = capi.Assets.TryGet(new AssetLocation(tex.Base.Domain, "textures/" + tex.Base.Path + ".png"))?.ToBitmap(capi);
        Shape? cube = capi.Assets.TryGet(new AssetLocation("game", "shapes/block/basic/cube.json"))?.ToObject<Shape>();
        if (bmp == null || cube == null) return;
        var opaque = (from x in Enumerable.Range(0, bmp.Width) from y in Enumerable.Range(0, bmp.Height)
                      where bmp.GetPixel(x, y).Alpha > 128 select (x, y)).ToArray();
        if (opaque.Length == 0) return;

        capi.BlockTextureAtlas.GetOrInsertTexture(tex.Base, out _, out seedTexPos);
        capi.Tesselator.TesselateShape(new TesselationMetaData { TypeForLogging = "sown seed", TexSource = this }, cube, out MeshData mesh);
        seedVoxelMeshes = SeedVoxels.Select((_, i) =>
        {
            // Pixels spread over the opaque ones, for a bit of colour variation between the voxels
            var (px, py) = opaque[(i + 1) * opaque.Length / (SeedVoxels.Length + 1)];
            float u = seedTexPos.x1 + (px + 0.5f) / bmp.Width * (seedTexPos.x2 - seedTexPos.x1);
            float v = seedTexPos.y1 + (py + 0.5f) / bmp.Height * (seedTexPos.y2 - seedTexPos.y1);
            MeshData voxel = mesh.Clone();
            for (int j = 0; j < voxel.VerticesCount; j++)
            {
                voxel.Uv[2 * j] = u;
                voxel.Uv[2 * j + 1] = v;
            }
            return voxel.Scale(new Vec3f(), 1 / 16f, 1 / 16f, 1 / 16f);
        }).ToArray();
        MarkDirty(true);
    }

    public TextureAtlasPosition this[string textureCode] => seedTexPos!;

    public Size2i AtlasSize => ((ICoreClientAPI)Api).BlockTextureAtlas.Size;

    // Seeds lie on the furrow floor; covering soil columns grow over them and hide them
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        bool skipDefault = base.OnTesselation(mesher, tessThreadTesselator);
        if (seedVoxelMeshes == null) return skipDefault;

        for (int i = 0; i < SpotCount; i++)
        {
            if (!seeded[i]) continue;
            var (cols, z) = Spot(i);
            for (int k = 0; k < SeedVoxels.Length; k++)
            {
                float across = (cols[0] + SeedVoxels[k].across) / 16f, along = (z + SeedVoxels[k].along) / 16f;
                mesher.AddMeshData(seedVoxelMeshes[k].Clone()
                    .Translate(Transposed ? along : across, TillingGrid.FurrowHeight / 16f, Transposed ? across : along));
            }
        }
        return skipDefault;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        byte[]? bytes = tree.GetBytes("seeded");
        if (bytes?.Length == SpotCount) seeded = bytes.Select(b => b != 0).ToArray();
        string? code = tree.GetString("seedCode");
        seedCode = code == null ? null : new AssetLocation(code);
        farmlandData = tree.GetTreeAttribute("farmland");
        base.FromTreeAttributes(tree, worldForResolving);
        GenSeedMesh();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBytes("seeded", seeded.Select(s => (byte)(s ? 1 : 0)).ToArray());
        if (seedCode != null) tree.SetString("seedCode", seedCode.ToString());
        if (farmlandData != null) tree["farmland"] = farmlandData;
    }
}
