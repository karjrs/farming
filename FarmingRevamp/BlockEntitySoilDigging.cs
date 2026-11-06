using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Soil being dug over with a shovel (single digging), split into 2×2 segments of 8×8 voxels. Holding right click on a
// segment first drives the spade in and levers the spit out (a blade-shaped hole a spade deep, its straight face on
// the player's side), then turns the spit over: a clod showing the smooth face the blade cut, 1–2 voxels above the
// ground. Once every segment is turned the block becomes dug soil, keeping this block entity and so its clods.
public class BlockEntitySoilDigging : BlockEntity
{
    const int StartPacket = 1100, StopPacket = 1101;
    public const int Segments = 2, SegmentSize = TillingGrid.Size / Segments;
    const int Ground = 16;
    const byte Untouched = 0, Hole = 1, Turned = 2;
    // Seconds per segment at mining speed 1 (shovels: stone 2.2 … steel 7), half to cut the spit, half to turn it
    const float BaseSegmentSeconds = 3f;

    readonly TillingGrid grid = new();
    // Per segment (sx * Segments + sz)
    readonly byte[] states = new byte[Segments * Segments];
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

    static IEnumerable<int> Columns(int segment) =>
        from i in Enumerable.Range(0, SegmentSize) from j in Enumerable.Range(0, SegmentSize)
        select (segment / Segments * SegmentSize + i) * TillingGrid.Size + segment % Segments * SegmentSize + j;

    static int SegmentOf(int x, int z) => x / SegmentSize * Segments + z / SegmentSize;

    // Server side, right after the block was placed
    public void Begin(Block from)
    {
        original = from.Code;
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
            float x = s / Segments * SegmentSize / 16f, z = s % Segments * SegmentSize / 16f, size = SegmentSize / 16f;
            return new Cuboidf(x, 0, z, x + size, Columns(s).Max(c => grid.Heights[c]) / 16f, z + size);
        }).ToArray();

        // The top of each segment still to dig
        var boxes = from s in Enumerable.Range(0, states.Length) where states[s] != Turned
            let x = s / Segments * SegmentSize let z = s % Segments * SegmentSize let top = Columns(s).Max(c => grid.Heights[c])
            select new[] { x, top - 1, z, x + SegmentSize, top, z + SegmentSize };
        outlineRenderer?.RegenMesh(boxes, Enumerable.Empty<int[]>(), false);
    }

    // Client side: the segments within a square `width` segments wide around the hit position that are still to dig
    public int[] SegmentsAround(BlockSelection blockSel, int width)
    {
        Vec3d hit = blockSel.HitPosition.SubCopy(blockSel.Face.Normald.Clone().Mul(0.001));
        width = Math.Clamp(width, 1, Segments);
        int sx = Math.Clamp((int)(hit.X * Segments) - (width - 1) / 2, 0, Segments - width);
        int sz = Math.Clamp((int)(hit.Z * Segments) - (width - 1) / 2, 0, Segments - width);
        return (from i in Enumerable.Range(sx, width) from j in Enumerable.Range(sz, width) select i * Segments + j)
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
        segments.Max(s => s / Segments) - segments.Min(s => s / Segments) < width && segments.Max(s => s % Segments) - segments.Min(s => s % Segments) < width;

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
            float x = (s / Segments + 0.5f) * SegmentSize / 16f, z = (s % Segments + 0.5f) * SegmentSize / 16f;
            Api.World.SpawnCubeParticles(Pos, Pos.ToVec3d().Add(x, 1, z), 0.25f, 6, 0.5f);
        }
        if (!changed) return;

        Api.World.PlaySoundAt(new AssetLocation("sounds/block/dirt1"), Pos, 0, null, true, 16, 0.5f);
        IPlayer player = holdPlayer;
        if (holdSegments.All(s => states[s] == Turned)) StopDigging();
        if (IsDone) Complete(player);
        Refresh();
        MarkDirty(true);
    }

    // The spade's hole: as wide as the blade (rounded corners) and a spit (~25 cm) deep at the straight face where it went
    // in (the player's side), shallower at the back where it levered the spit out; the edges beside the blade crumble
    void CutSpit(int s, IPlayer player) => Shape(s, player, (along, across) =>
    {
        if (across == 0 || across == SegmentSize - 1) return Ground - 1;
        int depth = across == 1 || across == SegmentSize - 2 ? 3 : 4;
        if (along >= SegmentSize - 2) depth -= along - (SegmentSize - 3);
        return Ground - depth;
    });

    // The spit turned over: a clod whose top is the smooth face the blade cut, tilted up away from the face it was
    // levered from, rounded at its corners
    void TurnSpit(int s, IPlayer player) => Shape(s, player, (along, across) =>
    {
        bool edgeAlong = along == 0 || along == SegmentSize - 1, edgeAcross = across == 0 || across == SegmentSize - 1;
        if (edgeAlong || edgeAcross) return Ground;
        bool corner = (along == 1 || along == SegmentSize - 2) && (across == 1 || across == SegmentSize - 2);
        return corner ? Ground + 1 : Ground + 1 + (along >= SegmentSize / 2 ? 1 : 0);
    });

    // Sets segment s to height(along, across), `along` counted from its side facing the player
    void Shape(int s, IPlayer player, System.Func<int, int, int> height)
    {
        int x0 = s / Segments * SegmentSize, z0 = s % Segments * SegmentSize;
        double dx = player.Entity.Pos.X - (Pos.X + (x0 + SegmentSize / 2.0) / 16), dz = player.Entity.Pos.Z - (Pos.Z + (z0 + SegmentSize / 2.0) / 16);
        bool alongX = Math.Abs(dx) > Math.Abs(dz);
        bool fromHigh = alongX ? dx > 0 : dz > 0;
        for (int i = 0; i < SegmentSize; i++)
        {
            for (int j = 0; j < SegmentSize; j++)
            {
                int a = alongX ? i : j, c = alongX ? j : i;
                grid[x0 + i, z0 + j] = height(fromHigh ? SegmentSize - 1 - a : a, c);
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
        string? from = tree.GetString("original");
        original = from == null ? null : new AssetLocation(from);
        if (Api != null) Refresh();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBytes("heights", grid.Heights);
        tree.SetBytes("segments", states);
        if (original != null) tree.SetString("original", original.ToString());
    }
}
