using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FarmingRevamp;

// Hoe: right click on dug soil starts tilling; holding right click on a field being worked (BlockEntityFurrowWork)
// digs its furrows or covers sown ones. Soil not dug over with a shovel yet is refused. Tool modes: paint works each furrow
// voxel the cursor is swept over, hold works the whole furrow clicked.
public class ItemTillingHoe : ItemHoe
{
    const int PaintMode = 0;
    SkillItem[] toolModes = Array.Empty<SkillItem>();

    // Client side: whether the current hold paints, and the last voxel it sent (to send each one once)
    static bool painting;
    static (BlockEntityFurrowWork be, int row, int z)? lastPainted;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        toolModes = new[]
        {
            new SkillItem { Code = new AssetLocation("paint"), Name = Lang.Get("farmingrevamp:toolmode-paint") },
            new SkillItem { Code = new AssetLocation("hold"), Name = Lang.Get("farmingrevamp:toolmode-hold") },
        };
        if (api is not ICoreClientAPI capi) return;
        toolModes[0].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("textures/icons/worldedit/chiselbrush.svg"), 48, 48, 5, ColorUtil.WhiteArgb));
        toolModes[1].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("textures/icons/lengthen.svg"), 48, 48, 5, ColorUtil.WhiteArgb));
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        base.OnUnloaded(api);
        foreach (SkillItem mode in toolModes) mode.Dispose();
    }

    public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel) => toolModes;

    public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) => slot.Itemstack?.Attributes.GetInt("toolMode") ?? PaintMode;

    public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel, int toolMode) => slot.Itemstack?.Attributes.SetInt("toolMode", toolMode);

    // shortcut: no use animation while working fields (only vanilla's Shift+Ctrl tilling keeps it); add one that fits the work later
    public override string? GetHeldTpUseAnimation(ItemSlot activeHotbarSlot, Entity forEntity) =>
        forEntity is EntityAgent { Controls: { ShiftKey: true, CtrlKey: true } } ? base.GetHeldTpUseAnimation(activeHotbarSlot, forEntity) : null;

    static bool IsPaintMode(ItemSlot slot) => slot.Itemstack?.Attributes.GetInt("toolMode") == PaintMode;

    public override void OnHeldInteractStart(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling)
    {
        IBlockAccessor ba = byEntity.World.BlockAccessor;
        if (blockSel != null && ba.GetBlockEntity(blockSel.Position) is BlockEntityFurrowWork be)
        {
            // Keeps the hold going (for as long as the button is held)
            handHandling = EnumHandHandling.PreventDefault;
            if (byEntity.World.Side != EnumAppSide.Client) return;
            painting = IsPaintMode(itemslot);
            if (painting)
            {
                Paint(be, blockSel);
                return;
            }
            int line = be.HitHoldLine(blockSel);
            if (be.CanHoe(line)) BlockEntityFurrowWork.StartHold(be, new[] { line });
            return;
        }

        Block? block = blockSel == null ? null : ba.GetBlock(blockSel.Position);
        // Vanilla handles everything else: ground storage, non-soil blocks, the "no block above" error
        // Only dug soil tills; soil still under sod (or being dug over) is refused
        bool dug = block?.Code?.Domain == "farmingrevamp" && block.FirstCodePart() == "dugsoil";
        bool sod = block != null && (CollectibleBehaviorSodDigging.Fertility(block) != null || block.Code?.Domain == "farmingrevamp" && block.FirstCodePart() == "diggingsoil");
        if (block == null || !dug && !sod || byEntity.Controls.ShiftKey && byEntity.Controls.CtrlKey || ba.GetBlock(blockSel!.Position.UpCopy()).Id != 0)
        {
            base.OnHeldInteractStart(itemslot, byEntity, blockSel!, entitySel, firstEvent, ref handHandling);
            return;
        }

        handHandling = EnumHandHandling.PreventDefault;
        if (sod)
        {
            (api as ICoreClientAPI)?.TriggerIngameError(this, "sod", Lang.Get("farmingrevamp:ingameerror-sod"));
            return;
        }

        // Painting goes on over the field the soil turns into
        if (byEntity.World.Side == EnumAppSide.Client) painting = IsPaintMode(itemslot);

        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        if (byEntity.World.Side != EnumAppSide.Server || byPlayer == null || !byEntity.World.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return;

        // Rows run along the direction the player is looking
        Vec3f view = byEntity.Pos.GetViewVector();
        string side = Math.Abs(view.X) > Math.Abs(view.Z) ? "we" : "ns";
        Block? tilling = byEntity.World.GetBlock(new AssetLocation("farmingrevamp", "tillingsoil-" + block.Variant["fertility"] + "-" + side));
        if (tilling == null) return;
        ba.SetBlock(tilling.BlockId, blockSel.Position);
        byEntity.World.PlaySoundAt(new AssetLocation("sounds/block/dirt1"), blockSel.Position, 0);
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
    {
        if (byEntity.World.Side == EnumAppSide.Client && painting)
        {
            if (blockSel != null && byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityFurrowWork pbe) Paint(pbe, blockSel);
            return true;
        }

        if (blockSel != null && byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityFurrowWork be)
        {
            // The server digs on its own clock and stops on the client's stop packet
            if (byEntity.World.Side != EnumAppSide.Client) return true;
            return be.IsHeld;
        }

        // Vanilla's step does the timed till; tilling now happens in the minigame
        return byEntity.Controls.ShiftKey && byEntity.Controls.CtrlKey && base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel!, entitySel);
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
    {
        StopDigging(byEntity);
        base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSel, entitySel);
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
    {
        StopDigging(byEntity);
        return true;
    }

    static void Paint(BlockEntityFurrowWork be, BlockSelection blockSel)
    {
        if (be.PaintVoxel(blockSel) is not var (row, z) || lastPainted == (be, row, z)) return;
        lastPainted = (be, row, z);
        be.SendPaint(row, z);
    }

    static void StopDigging(EntityAgent byEntity)
    {
        if (byEntity.World.Side != EnumAppSide.Client) return;
        painting = false;
        lastPainted = null;
        BlockEntityFurrowWork.StopHold();
    }
}
