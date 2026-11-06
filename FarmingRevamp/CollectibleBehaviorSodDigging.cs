using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Shovel: hold right click on grassy soil or forest floor to turn the sod over into bare soil.
public class CollectibleBehaviorSodDigging : CollectibleBehavior
{
    // Seconds to dig at mining speed 1; shovel's vanilla soil mining speed divides it (stone 2.2 … steel 7).
    const float BaseDigSeconds = 6f;

    public CollectibleBehaviorSodDigging(CollectibleObject collObj) : base(collObj) { }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (blockSel == null || DugCode(byEntity.World.BlockAccessor.GetBlock(blockSel.Position)) == null) return;

        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        if (byPlayer == null || !byEntity.World.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return;

        handHandling = EnumHandHandling.PreventDefault;
        handling = EnumHandling.PreventSubsequent;
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        // Other behaviors (seed covering) handle the rest
        if (blockSel == null || DugCode(byEntity.World.BlockAccessor.GetBlock(blockSel.Position)) == null) return false;
        handling = EnumHandling.PreventSubsequent;
        return secondsUsed < DigSeconds(slot, byEntity, blockSel);
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (blockSel == null || byEntity.World.Side != EnumAppSide.Server) return;
        if (secondsUsed < DigSeconds(slot, byEntity, blockSel)) return;

        IWorldAccessor world = byEntity.World;
        Block block = world.BlockAccessor.GetBlock(blockSel.Position);
        AssetLocation? dug = DugCode(block);
        Block? dugBlock = dug == null ? null : world.GetBlock(dug);
        if (dugBlock == null) return;

        handling = EnumHandling.PreventSubsequent;
        world.BlockAccessor.SetBlock(dugBlock.BlockId, blockSel.Position);
        world.PlaySoundAt(new AssetLocation("sounds/block/dirt1"), blockSel.Position, 0);
        collObj.DamageItem(world, byEntity, slot);
        TryDropStone(world, blockSel.Position);
        if (block.FirstCodePart() == "forestfloor") TryDropSticks(world, blockSel.Position);
    }

    // Forest floor hides fallen twigs: 15% chance of 1–2 sticks.
    static void TryDropSticks(IWorldAccessor world, BlockPos pos)
    {
        if (world.Rand.NextDouble() >= 0.15) return;
        Item? stick = world.GetItem(new AssetLocation("stick"));
        if (stick != null) world.SpawnItemEntity(new ItemStack(stick, 1 + world.Rand.Next(2)), pos.ToVec3d().Add(0.5, 1.1, 0.5));
    }

    // Chance of turning up a loose stone of the local rock (first block below with a rock variant: rock, gravel, sand…).
    static void TryDropStone(IWorldAccessor world, BlockPos pos)
    {
        for (int depth = 1; depth <= 16; depth++)
        {
            string? rock = world.BlockAccessor.GetBlock(pos.DownCopy(depth)).Variant["rock"];
            if (rock == null) continue;

            string? fertility = world.BlockAccessor.GetBlock(pos).Variant["fertility"];
            if (world.Rand.NextDouble() >= StoneChance(depth, fertility, rock, pos.Y - world.SeaLevel)) return;

            Item? stone = world.GetItem(new AssetLocation("stone-" + rock));
            if (stone != null) world.SpawnItemEntity(new ItemStack(stone), pos.ToVec3d().Add(0.5, 1.1, 0.5));
            return;
        }
    }

    // Shallow soil over rock is stony: 30% at depth ≤2 down to 3% at ≥6, scaled by fertility, rock hardness and altitude.
    static double StoneChance(int depth, string? fertility, string rock, int aboveSeaLevel)
    {
        double chance = 0.30 - 0.0675 * System.Math.Clamp(depth - 2, 0, 4);
        chance *= fertility switch { "verylow" => 2.0, "low" => 1.75, "compost" or "high" => 1.25, _ => 1.5 };
        chance *= rock switch
        {
            "granite" or "andesite" or "basalt" or "peridotite" or "chert" or "conglomerate" => 1.25, // hard, breaks into stones
            "chalk" or "limestone" or "claystone" or "shale" or "sandstone" => 0.75, // soft, weathers into fines
            _ => 1.0
        };
        chance *= 1 + System.Math.Clamp(aboveSeaLevel / 100.0, 0, 1); // up to x2 in the mountains
        return System.Math.Min(chance, 0.75);
    }

    float DigSeconds(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel)
    {
        Block block = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);
        float speed = collObj.GetMiningSpeed(slot.Itemstack!, blockSel, block, (byEntity as EntityPlayer)?.Player);
        return BaseDigSeconds / System.Math.Max(speed, 0.1f);
    }

    // Bare-soil block the sod turns into, or null if there is no sod to dig.
    internal static AssetLocation? DugCode(Block block)
    {
        if (block.Code == null || block.Code.Domain != "game") return null;
        if (block.FirstCodePart() == "forestfloor") return new AssetLocation("soil-verylow-none");
        if (block.FirstCodePart() == "soil" && block.Variant["grasscoverage"] is string grass && grass != "none") return block.CodeWithVariant("grasscoverage", "none");
        return null;
    }
}
