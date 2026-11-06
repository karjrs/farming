using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// A field block shown as a TillingGrid heightmap that a tool works by holding right click: each hold works the lines
// it started on along the rows from the far end, one step of the rows (HoldStep) per FurrowMs / FurrowLength;
// releasing pauses and the next hold resumes. Used for tilling (digging furrows) and sowing (collapsing the ridges back into them).
// In the hoe's paint mode (or by hand, see WorkWidth) single voxels are worked instead.
public abstract class BlockEntityFurrowWork : BlockEntity
{
    const int StartHoePacket = 1000, StopHoePacket = 1001, PaintPacket = 1003;
    // Time to work a whole furrow (the "hoe" animation loop is 1.33 s)
    protected const int FurrowMs = 1200;
    const int FurrowLength = TillingGrid.FurrowEndZ - TillingGrid.FurrowStartZ;

    protected TillingGrid Grid = new();
    TillingOutlineRenderer? outlineRenderer;
    public Cuboidf[] SelectionBoxes { get; private set; } = Array.Empty<Cuboidf>();

    // Server side: lines this hold works and the rows (z) it steps through, progress, start, who holds the tool and which
    int[] hoeLines = Array.Empty<int>();
    int[] hoeOrder = Array.Empty<int>();
    int hoeDone;
    long hoeStartMs;
    long hoeListenerId;
    IPlayer? hoePlayer;
    CollectibleObject? hoeTool;

    // Client side: the field and lines the local player's current hold works
    static BlockEntityFurrowWork? holdingIn;
    static int[] holdLines = Array.Empty<int>();

    // Grid rows run along z; on an east–west ("we") block the grid is shown with x and z swapped. The furrow pattern
    // is symmetric, so that looks the same as a 90° turn and matches the rotated farmland shape.
    protected bool Transposed => Block?.Variant["side"] == "we";

    // Block whose plain cube mesh (and textures) the heightmap columns are cut from
    protected abstract Block? ColumnBlock { get; }

    protected abstract Cuboidf[] BuildSelectionBoxes();

    // Lines the tools work (grid columns along the rows): furrows or ridges
    protected abstract int[][] Lines { get; }

    // The line at grid position x across the rows, or -1
    protected virtual int LineAt(double x) => Array.FindIndex(Lines, cols => cols.Contains(Math.Clamp((int)x, 0, TillingGrid.Size - 1)));

    // The line a hoe hold at grid position x works
    protected virtual int HoldLineAt(double x) => LineAt(x);

    // Voxel boxes in grid coordinates (x1, y1, z1, x2, y2, z2) to outline like the knapping recipe
    protected abstract IEnumerable<int[]> OutlineBoxes();

    // Boxes that may be worked but need not be, outlined less prominently
    protected virtual IEnumerable<int[]> OptionalOutlineBoxes() => Enumerable.Empty<int[]>();

    // Whether line voxel (line, z) is still to be worked
    protected abstract bool CanWork(int line, int z);

    // Whether the field takes work at all yet
    protected virtual bool Workable => true;

    // How many grid columns across the tool (null: bare hand) works at once; 0 if it cannot work this field. A bare
    // hand only works single voxels, by clicking.
    public virtual int WorkWidth(ItemStack? tool) =>
        tool?.Collectible is ItemTillingHoe ? 1 : tool?.Collectible.GetBehavior<CollectibleBehaviorSeedCovering>()?.Width ?? 0;

    public bool CanHoe(int line) => Workable && line >= 0 && line < Lines.Length && Rows.Any(z => CanWork(line, z));

    protected static IEnumerable<int> Rows => Enumerable.Range(TillingGrid.FurrowStartZ, TillingGrid.FurrowEndZ - TillingGrid.FurrowStartZ);

    // One voxel outline per line voxel still to work, the voxel at height y..y+1
    protected IEnumerable<int[]> LineVoxelBoxes(int y) =>
        from line in Enumerable.Range(0, Lines.Length) from z in Rows where CanWork(line, z)
        from x in Lines[line] select new[] { x, y, z, x + 1, y + 1, z + 1 };

    // The tool is pulled towards the player: from the far end of the rows, all lines at once, skipping rows already worked
    IEnumerable<int> HoeOrder(IPlayer player, int[] lines) =>
        (PlayerAtRowStart(player) ? Rows.Reverse() : Rows).Where(z => lines.Any(line => CanWork(line, z)));

    // Returns false if (row, z) was already worked
    protected abstract bool HoeStep(int row, int z);

    // Works row z of a hold's lines with `tool`; returns the lines that changed
    protected virtual int[] HoldStep(CollectibleObject tool, int[] lines, int z) => lines.Where(line => CanWork(line, z) && HoeStep(line, z)).ToArray();

    protected abstract bool IsComplete { get; }

    // Server side, once IsComplete; replaces this block
    protected abstract void OnComplete(IPlayer player);

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

    // After the grid changed: selection boxes, outline, and (server) sync and redraw
    protected void Refresh()
    {
        SelectionBoxes = BuildSelectionBoxes();
        outlineRenderer?.RegenMesh(OutlineBoxes(), OptionalOutlineBoxes(), Transposed);
    }

    // Selection box over grid columns x1..x2, z1..z2, as tall as the highest column (or up to y2 if given)
    protected Cuboidf Box(int x1, int x2, int z1, int z2, int y2 = -1)
    {
        if (y2 < 0)
        {
            for (int x = x1; x < x2; x++)
            {
                for (int z = z1; z < z2; z++) y2 = Math.Max(y2, Grid[x, z]);
            }
        }
        return Transposed
            ? new Cuboidf(z1 / 16f, 0, x1 / 16f, z2 / 16f, y2 / 16f, x2 / 16f)
            : new Cuboidf(x1 / 16f, 0, z1 / 16f, x2 / 16f, y2 / 16f, z2 / 16f);
    }

    // Boxes that only make the rest of the field selectable: whole columns, or only the ends of columns whose middle
    // (along the furrows) the caller boxed already
    protected void AddFieldBoxes(List<Cuboidf> boxes, System.Func<int, bool> boxedMiddle)
    {
        for (int x = 0; x < TillingGrid.Size; x++)
        {
            if (!boxedMiddle(x))
            {
                boxes.Add(Box(x, x + 1, 0, TillingGrid.Size));
                continue;
            }
            boxes.Add(Box(x, x + 1, 0, TillingGrid.FurrowStartZ));
            boxes.Add(Box(x, x + 1, TillingGrid.FurrowEndZ, TillingGrid.Size));
        }
    }

    // Whether the player stands at the low-z end of the rows (low-x on a transposed block)
    protected bool PlayerAtRowStart(IPlayer player) => Transposed ? player.Entity.Pos.X < Pos.X + 0.5 : player.Entity.Pos.Z < Pos.Z + 0.5;

    // Client side; the server-side hit position is inaccurate, so the client sends the lines (like knapping).
    public static void StartHold(BlockEntityFurrowWork be, int[] lines)
    {
        holdingIn = be;
        holdLines = lines;
        be.SendPacket(StartHoePacket, lines.Select(line => (byte)line).ToArray());
    }

    // Client side: whether the local player's hold is on this field and has work left
    public bool IsHeld => this == holdingIn && holdLines.Any(CanHoe);

    // Client side, also when the player looked away mid-hold: the server keeps working until told to stop
    public static void StopHold()
    {
        holdingIn?.SendPacket(StopHoePacket, Array.Empty<byte>());
        holdingIn = null;
    }

    // Hit position in grid voxels (across, along); nudged inwards so a hit on a box edge counts for the voxel behind it
    (double x, double z) GridHit(BlockSelection blockSel)
    {
        Vec3d hit = blockSel.HitPosition.SubCopy(blockSel.Face.Normald.Clone().Mul(0.001));
        return ((Transposed ? hit.Z : hit.X) * 16, (Transposed ? hit.X : hit.Z) * 16);
    }

    // Client side: the line a hoe hold at the hit position works, or -1
    public int HitHoldLine(BlockSelection blockSel) => HoldLineAt(GridHit(blockSel).x);

    // Client side: the line voxel (row, z) at the hit position, or null if there is none to work there
    public (int row, int z)? PaintVoxel(BlockSelection blockSel)
    {
        var (hx, hz) = GridHit(blockSel);
        int z = Math.Clamp((int)hz, 0, TillingGrid.Size - 1), row = LineAt(hx);
        return InRows(z) && CanHoe(row) && CanWork(row, z) ? (row, z) : null;
    }

    // Client side: lines with work left that lie within a band `width` columns wide centred on the hit position
    public int[] LinesAround(BlockSelection blockSel, int width)
    {
        double hx = GridHit(blockSel).x;
        return Enumerable.Range(0, Lines.Length)
            .Where(line => CanHoe(line) && Lines[line].All(x => Math.Abs(x + 0.5 - hx) < width / 2.0))
            .ToArray();
    }

    static bool InRows(int z) => z >= TillingGrid.FurrowStartZ && z < TillingGrid.FurrowEndZ;

    public void SendPaint(int row, int z) => SendPacket(PaintPacket, new[] { (byte)row, (byte)z });

    protected void SendPacket(int packetId, byte[] data) => ((ICoreClientAPI)Api).Network.SendBlockEntityPacket(Pos, packetId, data);

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(player, packetid, data);
        if (packetid == StopHoePacket && player == hoePlayer)
        {
            StopHoe();
            return;
        }

        if (packetid == PaintPacket)
        {
            if (data.Length != 2 || !InRows(data[1]) || !CanHoe(data[0])) return;
            if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.BuildOrBreak)) return;
            if (WorkWidth(player.InventoryManager.ActiveHotbarSlot.Itemstack) == 0) return;
            // shortcut: no rate limit, a fast sweep (or a modded client) digs as fast as it sends; add one if that matters
            if (!HoeStep(data[0], data[1])) return;
            OnWorked(new int[] { data[0] }, data[1]);
            AfterWork(player);
            return;
        }

        if (packetid != StartHoePacket || data.Length == 0 || data.Distinct().Count() != data.Length || !data.All(line => CanHoe(line)) || hoePlayer != null) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.BuildOrBreak)) return;
        ItemStack? tool = player.InventoryManager.ActiveHotbarSlot.Itemstack;
        var cols = data.SelectMany(line => Lines[line]).ToArray();
        // One line always fits the tool (a furrow is wider than the hoe's one column); more must lie within its width
        if (tool == null || WorkWidth(tool) == 0 || data.Length > 1 && cols.Max() - cols.Min() >= WorkWidth(tool)) return;

        hoeLines = data.Select(line => (int)line).ToArray();
        hoeOrder = HoeOrder(player, hoeLines).ToArray();
        if (hoeOrder.Length == 0) return;
        hoeDone = 0;
        hoeStartMs = Api.World.ElapsedMilliseconds;
        hoePlayer = player;
        hoeTool = tool.Collectible;
        hoeListenerId = RegisterGameTickListener(OnHoeTick, 20);
    }

    void OnHoeTick(float dt)
    {
        if (hoePlayer == null || hoePlayer.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible != hoeTool)
        {
            StopHoe();
            return;
        }

        long heldMs = Api.World.ElapsedMilliseconds - hoeStartMs;
        int target = Math.Min(hoeOrder.Length, 1 + (int)(heldMs * FurrowLength / FurrowMs));
        if (target <= hoeDone) return;

        int[] worked = Array.Empty<int>();
        for (; hoeDone < target; hoeDone++) worked = HoldStep(hoeTool!, hoeLines, hoeOrder[hoeDone]);
        OnWorked(worked, hoeOrder[hoeDone - 1]);
        IPlayer player = hoePlayer;
        if (hoeDone == hoeOrder.Length) StopHoe();
        AfterWork(player);
    }

    // Particles at the worked voxels of row z and a sound
    void OnWorked(int[] lines, int z)
    {
        if (lines.Length == 0) return;
        foreach (int line in lines)
        {
            float across = (Lines[line][0] + Lines[line].Length / 2f) / 16f, along = (z + 0.5f) / 16f;
            Api.World.SpawnCubeParticles(Pos, Pos.ToVec3d().Add(Transposed ? along : across, 1, Transposed ? across : along), 0.15f, 3, 0.4f);
        }
        Api.World.PlaySoundAt(new AssetLocation("sounds/block/dirt1"), Pos, 0, null, true, 16, 0.3f);
    }

    // Completes the field once done (wearing the tool, if any), otherwise syncs it
    void AfterWork(IPlayer player)
    {
        if (IsComplete)
        {
            StopHoe();
            OnComplete(player);
            ItemSlot toolSlot = player.InventoryManager.ActiveHotbarSlot;
            toolSlot.Itemstack?.Collectible.DamageItem(Api.World, player.Entity, toolSlot);
            return;
        }

        Refresh();
        MarkDirty(true);
    }

    void StopHoe()
    {
        if (hoePlayer == null) return;
        UnregisterGameTickListener(hoeListenerId);
        hoePlayer = null;
    }

    // Renders the grid as columns cut from ColumnBlock's cube, scaled to the grid heights.
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        Block? columnBlock = ColumnBlock;
        if (columnBlock == null) return false;
        SoilColumnMesh.Add(mesher, tessThreadTesselator, columnBlock, Grid, Transposed);
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        byte[]? heights = tree.GetBytes("heights");
        if (heights?.Length == Grid.Heights.Length) heights.CopyTo(Grid.Heights, 0);
        if (Api != null) Refresh();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBytes("heights", Grid.Heights);
    }
}
