using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FoodPerishStorageTag
{
    public class FoodPerishStorageTagModSystem : ModSystem
    {
        // Create Dictionary to track when players log on
        public static Dictionary<string, double> PlayerSessionStart  = new Dictionary<string, double>();

        public override void Start(ICoreAPI api)
        {
            // Register new storage container behavior
            api.RegisterBlockEntityBehaviorClass("OfflinePreserve", typeof(BEBehaviorOfflinePreserve));
            api.RegisterBlockBehaviorClass("OfflinePreserveWatcher", typeof(BlockBehaviorPreserveWatcher));
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            // Attach new behavior to containers
            foreach (var block in api.World.Blocks)
            {
                if (block.Code == null) continue;

                if (block.GetBehavior<BlockBehaviorContainer>() != null &&
                    !block.HasBehavior<BlockBehaviorPreserveWatcher>())
                {
                    block.BlockEntityBehaviors = block.BlockEntityBehaviors
                        .Prepend(new BlockEntityBehaviorType { Name = "OfflinePreserve", properties = null })
                        .ToArray();

                    block.BlockBehaviors = block.BlockBehaviors
                        .Prepend(new BlockBehaviorPreserveWatcher(block))
                        .ToArray();
                }
            }
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            // Track session time by subscribing to PlayerJoin and PlayerDisconnect events and attaching Lambda functions
            api.Event.PlayerJoin += player => PlayerSessionStart[player.PlayerUID] = api.World.Calendar.TotalHours;
            api.Event.PlayerDisconnect += player => PlayerSessionStart.Remove(player.PlayerUID);

            // Save owner on placement of block by subscribing to DidPlaceBlock event
            api.Event.DidPlaceBlock += (player, oldId, sel, stack) => {
                var blockentity = player.Entity.World.BlockAccessor.GetBlockEntity(sel.Position);

                if (blockentity is BlockEntityContainer && blockentity.GetBehavior<BEBehaviorOfflinePreserve>() is BEBehaviorOfflinePreserve behavior)
                {
                    behavior.OwnerUID = player.PlayerUID;

                    Mod.Logger.Notification($"Owner set to {player.PlayerName} at {sel.Position}");

                    blockentity.MarkDirty(); // Save to disk
                }
            };
        }

        public class BlockBehaviorPreserveWatcher : BlockBehavior
        {
            public BlockBehaviorPreserveWatcher(Block block) : base(block) { }

            public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
            {
                var blockentity = world.BlockAccessor.GetBlockEntity(blockSel.Position);
                var behavior = blockentity?.GetBehavior<BEBehaviorOfflinePreserve>();

                if (behavior != null)
                {
                    behavior.UpdateLastUser(byPlayer);
                }

                return base.OnBlockInteractStart(world, byPlayer, blockSel, ref handling);
            }
        }

        public class BEBehaviorOfflinePreserve : BlockEntityBehavior
        {
            public string? OwnerUID;
            public string? LastUserUID;
            public double LastSaveTime; // Store when the chunk last unloaded
            public BEBehaviorOfflinePreserve(BlockEntity entity) : base(entity) { }

            public void UpdateLastUser(IPlayer player)
            {
                if (LastUserUID != player.PlayerUID)
                {
                    LastUserUID = player.PlayerUID;

                    Api.Logger.Notification($"Last User updated to {player.PlayerName} at {Blockentity.Pos}");

                    Blockentity.MarkDirty();
                }
            }

            public override void Initialize(ICoreAPI api, JsonObject properties)
            {
                base.Initialize(api, properties);

                if (Blockentity is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed += (transType, stack, baseMul) =>
                    {
                        if (api.Side == EnumAppSide.Client) return baseMul;

                        string? targetUid = LastUserUID ?? OwnerUID;

                        if (targetUid == null || !IsPlayerActive(api, targetUid)) return 0f;

                        if (FoodPerishStorageTagModSystem.PlayerSessionStart.TryGetValue(targetUid, out double sessionStart))
                        {

                            if (sessionStart > LastSaveTime)
                            {
                                return 0f;
                            }
                        }

                        return baseMul;
                    };
                }
            }


            private bool IsPlayerActive(ICoreAPI api, string? uid)
            {
                if (uid == null) return false;
                if (api.World.PlayerByUid(uid) == null) return false;
                if (!PlayerSessionStart.ContainsKey(uid)) return false;
                return true;
            }
            public override void ToTreeAttributes(ITreeAttribute tree)
            {
                base.ToTreeAttributes(tree);
                if (OwnerUID != null) { tree.SetString("uid", OwnerUID); }
                if (LastUserUID != null) { tree.SetString("lastUserUid", LastUserUID); }

                // Save the current time when the chunk unloads/saves
                tree.SetDouble("lastSaveTime", Api.World.Calendar.TotalHours);
            }
            public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
            {
                base.FromTreeAttributes(tree, worldAccessForResolve);
                OwnerUID = tree.GetString("uid");
                LastUserUID = tree.GetString("lastUserUid");

                // Load the time
                LastSaveTime = tree.GetDouble("lastSaveTime");
            }
        }
    }
}
