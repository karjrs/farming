using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Soil being dug over with a shovel (single digging), split into 2 columns × 4 rows of segments, each a spit 8 voxels
// across (the blade) and 4 along the rows; rows run the way the player looked when starting. Holding right click on a
// segment first drives the spade in and levers the spit out (a steep hole a spade deep), then turns the spit over: a
// clod showing the smooth face the blade cut, 1–2 voxels above the ground; a few voxels of both vary at random.
// Once every segment is turned the block becomes dug soil, keeping this block entity and so its clods.
public class BlockEntitySoilDigging : BlockEntity
{
    const int StartPacket = 1100, StopPacket = 1101;
    const int Cols = 2, Rows = 4, Across = TillingGrid.Size / Cols, Along = TillingGrid.Size / Rows;
    const int Ground = 16;
    const byte Untouched = 0, Hole = 1, Turned = 2;
    // Seconds per segment at mining speed 1 (shovels: stone 2.2 … steel 7), half to cut the spit, half to turn it
    const float BaseSegmentSeconds = 3f;

    readonly TillingGrid grid = new();
    // Per segment (col * Rows + row)
    readonly byte[] states = new byte[Cols * Rows];
    // Whether the rows run along x (else along z)
    bool alongX;
    // Block the soil was dug from (grass, forest floor); untouched segments still look like it
    AssetLocation? original;

    TillingOutlineRenderer? outlineRenderer;
    public Cuboidf[] SelectionBoxes { get; private set; } = Array.Empty<Cuboidf>();

    // Server side: the segments this hold digs, their states when it started, the clock and who holds what
    int[] holdSegments = Array.Empty<int>();
    byte[] holdFrom = Array.Empty<byte>();
    long holdStartMs;
    float phaseMs;
    long holdListenerId;
    IPlayer? holdPlayer;
    CollectibleObject? holdTool;

    // Client side: the field and segments the local player's current hold digs
    static BlockEntitySoilDigging? holdingIn;
    static int[] heldSegments = Array.Empty<int>();

    public BlockEntitySoilDigging() => Array.Fill(grid.Heights, (byte)Ground);

    public bool IsDone => states.All(s => s == Turned);

    // Grid rectangle of segment s: corner and size in x and z
    (int x0, int z0, int w, int d) Rect(int s)
    {
        int across = s / Rows * Across, along = s % Rows * Along;
        return alongX ? (along, across, Along, Across) : (across, along, Across, Along);
    }

    IEnumerable<int> Columns(int s)
    {
        var (x0, z0, w, d) = Rect(s);
        return from x in Enumerable.Range(x0, w) from z in Enumerable.Range(z0, d) select x * TillingGrid.Size + z;
    }

    int SegmentOf(int x, int z) => alongX ? z / Across * Rows + x / Along : x / Across * Rows + z / Along;

    // Server side, right after the block was placed
    public void Begin(Block from, bool rowsAlongX)
    {
        original = from.Code;
        alongX = rowsAlongX;
        MarkDirty(true);
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi) outlineRenderer = new TillingOutlineRenderer(Pos, capi);
        Refresh();
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        outlineRenderer?.Dispose();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        outlineRenderer?.Dispose();
    }

    void Refresh()
    {
        SelectionBoxes = Enumerable.Range(0, states.Length).Select(s =>
        {
            var (x0, z0, w, d) = Rect(s);
            return new Cuboidf(x0 / 16f, 0, z0 / 16f, (x0 + w) / 16f, Columns(s).Max(c => grid.Heights[c]) / 16f, (z0 + d) / 16f);
        }).ToArray();

        // The top of each segment still to dig
        var boxes = from s in Enumerable.Range(0, states.Length) where states[s] != Turned
            let r = Rect(s) let top = Columns(s).Max(c => grid.Heights[c])
            select new[] { r.x0, top - 1, r.z0, r.x0 + r.w, top, r.z0 + r.d };
        outlineRenderer?.RegenMesh(boxes, Enumerable.Empty<int[]>(), false);
    }

    // Client side: the segments still to dig within `width` columns and rows around the hit position
    public int[] SegmentsAround(BlockSelection blockSel, int width)
    {
        Vec3d hit = blockSel.HitPosition.SubCopy(blockSel.Face.Normald.Clone().Mul(0.001));
        int segment = SegmentOf(Math.Clamp((int)(hit.X * 16), 0, 15), Math.Clamp((int)(hit.Z * 16), 0, 15));
        int cols = Math.Min(width, Cols), rows = Math.Min(width, Rows);
        int col = Math.Clamp(segment / Rows - (cols - 1) / 2, 0, Cols - cols), row = Math.Clamp(segment % Rows - (rows - 1) / 2, 0, Rows - rows);
        return (from c in Enumerable.Range(col, cols) from r in Enumerable.Range(row, rows) select c * Rows + r)
            .Where(s => states[s] != Turned).ToArray();
    }

    // Client side; the server-side hit position is inaccurate, so the client sends the segments (like knapping)
    public static void StartHold(BlockEntitySoilDigging be, int[] segments)
    {
        holdingIn = be;
        heldSegments = segments;
        be.SendPacket(StartPacket, segments.Select(s => (byte)s).ToArray());
    }

    // Client side: whether the local player's hold is on this block and has digging left
    public bool IsHeld => this == holdingIn && heldSegments.Any(s => states[s] != Turned);

    public static void StopHold()
    {
        holdingIn?.SendPacket(StopPacket, Array.Empty<byte>());
        holdingIn = null;
    }

    void SendPacket(int packetId, byte[] data) => ((ICoreClientAPI)Api).Network.SendBlockEntityPacket(Pos, packetId, data);

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(player, packetid, data);
        if (packetid == StopPacket && player == holdPlayer)
        {
            StopDigging();
            return;
        }

        if (packetid != StartPacket || holdPlayer != null || data.Length == 0 || data.Distinct().Count() != data.Length) return;
        if (!data.All(s => s < states.Length && states[s] != Turned)) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.BuildOrBreak)) return;
        ItemStack? tool = player.InventoryManager.ActiveHotbarSlot.Itemstack;
        int width = tool?.Collectible.GetBehavior<CollectibleBehaviorSodDigging>()?.Width ?? 0;
        if (tool == null || width == 0 || !FitsWidth(data, width)) return;

        Block soil = (original == null ? null : Api.World.GetBlock(original)) ?? Block;
        float speed = tool.Collectible.GetMiningSpeed(tool, new BlockSelection { Position = Pos }, soil, player);
        phaseMs = BaseSegmentSeconds * 1000 / 2 / Math.Max(speed, 0.1f);
        holdSegments = data.Select(s => (int)s).ToArray();
        holdFrom = holdSegments.Select(s => states[s]).ToArray();
        holdStartMs = Api.World.ElapsedMilliseconds;
        holdPlayer = player;
        holdTool = tool.Collectible;
        holdListenerId = RegisterGameTickListener(OnDigTick, 20);
    }

    static bool FitsWidth(byte[] segments, int width) =>
        segments.Max(s => s / Rows) - segments.Min(s => s / Rows) < width && segments.Max(s => s % Rows) - segments.Min(s => s % Rows) < width;

    void OnDigTick(float dt)
    {
        if (holdPlayer == null || holdPlayer.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible != holdTool)
        {
            StopDigging();
            return;
        }

        int phases = (int)((Api.World.ElapsedMilliseconds - holdStartMs) / phaseMs);
        bool changed = false;
        for (int i = 0; i < holdSegments.Length; i++)
        {
            int s = holdSegments[i];
            byte target = (byte)Math.Min(Turned, holdFrom[i] + phases);
            if (states[s] >= target) continue;
            if (target == Hole) CutSpit(s, holdPlayer); else TurnSpit(s, holdPlayer);
            states[s] = target;
            changed = true;
            var (x0, z0, w, d) = Rect(s);
            Api.World.SpawnCubeParticles(Pos, Pos.ToVec3d().Add((x0 + w / 2f) / 16, 1, (z0 + d / 2f) / 16), 0.25f, 6, 0.5f);
        }
        if (!changed) return;

        Api.World.PlaySoundAt(new AssetLocation("sounds/block/dirt1"), Pos, 0, null, true, 16, 0.5f);
        IPlayer player = holdPlayer;
        if (holdSegments.All(s => states[s] == Turned)) StopDigging();
        if (IsDone) Complete(player);
        Refresh();
        MarkDirty(true);
    }

    // The spade's hole: steep-sided, a spit (~25 cm) deep across the blade's width; the soil beside the blade crumbles
    // in a little, and some crumbs fall to the bottom
    void CutSpit(int s, IPlayer player) => Shape(s, player, (along, across) =>
    {
        if (across == 0 || across == Across - 1) return Chance(0.35) ? Ground : Ground - 1;
        return Ground - 4 + (Chance(0.2) ? 1 : 0);
    });

    // The spit turned over: a clod whose top is the smooth face the blade cut, tilted up away from the player and
    // rounded at its corners, sitting in the hole; some voxels stick out or crumble off
    void TurnSpit(int s, IPlayer player) => Shape(s, player, (along, across) =>
    {
        bool edge = along == 0 || along == Along - 1 || across == 0 || across == Across - 1;
        if (edge) return Chance(0.2) ? Ground + 1 : Ground;
        bool corner = across == 1 || across == Across - 2;
        int height = Ground + 1 + (!corner && along >= Along / 2 ? 1 : 0);
        return Chance(0.15) ? height + (Chance(0.5) ? 1 : -1) : height;
    });

    bool Chance(double p) => Api.World.Rand.NextDouble() < p;

    // Sets segment s to height(along, across), `along` counted from its end nearer the player
    void Shape(int s, IPlayer player, System.Func<int, int, int> height)
    {
        var (x0, z0, w, d) = Rect(s);
        double toPlayer = alongX ? player.Entity.Pos.X - (Pos.X + (x0 + w / 2.0) / 16) : player.Entity.Pos.Z - (Pos.Z + (z0 + d / 2.0) / 16);
        for (int x = x0; x < x0 + w; x++)
        {
            for (int z = z0; z < z0 + d; z++)
            {
                int along = alongX ? x - x0 : z - z0, across = alongX ? z - z0 : x - x0;
                grid[x, z] = height(toPlayer > 0 ? Along - 1 - along : along, across);
            }
        }
    }

    void Complete(IPlayer player)
    {
        Block? dug = Api.World.GetBlock(new AssetLocation("farmingrevamp", "dugsoil-" + Block.Variant["fertility"]));
        if (dug == null) return;
        // Exchange keeps this block entity and so the clods
        Api.World.BlockAccessor.ExchangeBlock(dug.BlockId, Pos);
        ItemSlot toolSlot = player.InventoryManager.ActiveHotbarSlot;
        toolSlot.Itemstack?.Collectible.DamageItem(Api.World, player.Entity, toolSlot);
    }

    void StopDigging()
    {
        if (holdPlayer == null) return;
        UnregisterGameTickListener(holdListenerId);
        holdPlayer = null;
    }

    // Untouched segments are cut from the original block (grass on top), the dug ones from bare soil
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        Block? soil = Api.World.GetBlock(new AssetLocation("soil-" + Block.Variant["fertility"] + "-none"));
        if (soil == null) return false;
        Block? from = original == null ? null : Api.World.GetBlock(original);
        SoilColumnMesh.Add(mesher, tessThreadTesselator, soil, grid, false, (x, z) => from == null || states[SegmentOf(x, z)] != Untouched);
        if (from == null) return true;
        SoilColumnMesh.Add(mesher, tessThreadTesselator, from, grid, false, (x, z) => states[SegmentOf(x, z)] == Untouched);
        SoilColumnMesh.AddTopsoilOverlay((ICoreClientAPI)Api, mesher, tessThreadTesselator, from, Pos, grid, (x, z) => states[SegmentOf(x, z)] == Untouched);
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        byte[]? heights = tree.GetBytes("heights");
        if (heights?.Length == grid.Heights.Length) heights.CopyTo(grid.Heights, 0);
        byte[]? segments = tree.GetBytes("segments");
        if (segments?.Length == states.Length) segments.CopyTo(states, 0);
        alongX = tree.GetBool("alongX");
        string? from = tree.GetString("original");
        original = from == null ? null : new AssetLocation(from);
        if (Api != null) Refresh();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBytes("heights", grid.Heights);
        tree.SetBytes("segments", states);
        tree.SetBool("alongX", alongX);
        if (original != null) tree.SetString("original", original.ToString());
    }
}
