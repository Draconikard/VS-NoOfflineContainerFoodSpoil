using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace NoOfflineContainerFoodSpoil
{
    public class NoOfflineContainerFoodSpoilModSystem : ModSystem
    {
        // Attendance sheet for the server. Tracks what time players logged into the server.
        public static Dictionary<string, double> PlayerSessionStart  = new Dictionary<string, double>();

        // Registers two behaviors on the Client and Server
        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockEntityBehaviorClass("OfflinePreserve", typeof(BEBehaviorOfflinePreserve));
            api.RegisterBlockBehaviorClass("OfflinePreserveWatcher", typeof(BlockBehaviorPreserveWatcher));
        }

        // Attaches the registered behaviors to all "Container" blocks.
        public override void AssetsFinalize(ICoreAPI api)
        {
            foreach (var block in api.World.Blocks)
            {
                if (block.Code == null) continue;

                if (block.GetBehavior<BlockBehaviorContainer>() != null &&
                    !block.HasBehavior<BlockBehaviorPreserveWatcher>())
                {
                    // Prepends instead of appends to avoid having data overwritten.
                    block.BlockEntityBehaviors = block.BlockEntityBehaviors
                        .Prepend(new BlockEntityBehaviorType { Name = "OfflinePreserve", properties = null })
                        .ToArray();

                    block.BlockBehaviors = block.BlockBehaviors
                        .Prepend(new BlockBehaviorPreserveWatcher(block))
                        .ToArray();
                }
            }
        }

        // Server-only logic
        public override void StartServerSide(ICoreServerAPI api)
        {
            // Updates PlayerSessionStart dictionary initialized earlier
            api.Event.PlayerJoin += player => PlayerSessionStart[player.PlayerUID] = api.World.Calendar.TotalHours;
            api.Event.PlayerDisconnect += player => PlayerSessionStart.Remove(player.PlayerUID);

            // Container blocks placed by players have themselves assigned as their owner.
            api.Event.DidPlaceBlock += (player, oldId, sel, stack) => {
                var blockentity = player.Entity.World.BlockAccessor.GetBlockEntity(sel.Position);

                if (blockentity is BlockEntityContainer && blockentity.GetBehavior<BEBehaviorOfflinePreserve>() is BEBehaviorOfflinePreserve behavior)
                {
                    behavior.OwnerUID = player.PlayerUID;

                    Mod.Logger.Notification($"Owner set to {player.PlayerName} <{player.PlayerUID}> at {sel.Position}");

                    blockentity.MarkDirty(); // Saves information to disk.
                }
            };

            // Auditing log that announces when a player breaks a container block that was not owned by them.
            api.Event.BreakBlock += (IServerPlayer player, BlockSelection blockSel, ref float dropMul, ref EnumHandling handling) =>
            {
                var blockentity = api.World.BlockAccessor.GetBlockEntity(blockSel.Position);

                if (blockentity?.GetBehavior<BEBehaviorOfflinePreserve>() is BEBehaviorOfflinePreserve behavior)
                {
                    if (!string.IsNullOrEmpty(behavior.OwnerUID) && behavior.OwnerUID != player.PlayerUID)
                    {
                        Mod.Logger.Audit($"Player {player.PlayerName} <{player.PlayerUID}> has broken a container block that was owned by player {api.PlayerData.GetPlayerDataByUid(behavior.OwnerUID).LastKnownPlayername} <{behavior.OwnerUID}> at {behavior.Pos}");
                    }
                }
            };
        }

        // Custom BlockBehavior Class that watches container blocks to track who last interacted with them.
        public class BlockBehaviorPreserveWatcher : BlockBehavior
        {
            public BlockBehaviorPreserveWatcher(Block block) : base(block) { } // Initialization

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

        // Custom BlockEntityBehavior that updates database with last user and owner, as well as prevents spoilage if the owner of the container is offline.
        public class BEBehaviorOfflinePreserve : BlockEntityBehavior
        {
            public string? OwnerUID;
            public string? LastUserUID;
            public double LastChunkLoadTime;
            public BEBehaviorOfflinePreserve(BlockEntity entity) : base(entity) { }

            // Updates the block's last user if it doesn't equal the owner. This is called by the Block BehaviorPreserverWatcher.
            public void UpdateLastUser(IPlayer player)
            {
                if (LastUserUID != player.PlayerUID)
                {
                    LastUserUID = player.PlayerUID;

                    Api.Logger.Notification($"Last User updated to {player.PlayerName} <{player.PlayerUID}> at {Blockentity.Pos}");

                    Blockentity.MarkDirty();
                }
            }

            // Override the blocks Initialize method to perform spoilage prevention logic
            public override void Initialize(ICoreAPI api, JsonObject properties)
            {
                base.Initialize(api, properties); // Tacks on Initialize's existing behavior first.

                if (Blockentity is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed += (transType, stack, baseMul) => // This event is called when a container is loaded into a chunk.
                    {
                        if (api.Side == EnumAppSide.Client) return baseMul; // Doesn't do anything if this is being called by a client.

                        string? targetUid = LastUserUID ?? OwnerUID;

                        if (targetUid == null || !IsPlayerActive(api, targetUid)) return 0f; // Checks if owner is active via IsPlayerActive method.

                        if (PlayerSessionStart.TryGetValue(targetUid, out double sessionStart))
                        {
                            if (sessionStart > LastChunkLoadTime)
                            {
                                return 0f; // Returns spoilage value of 0 if owner loads into unloaded chunk
                            }
                        }

                        return baseMul;
                    };
                }
            }

            // Checks to see if a player is online via uid.
            private bool IsPlayerActive(ICoreAPI api, string? uid)
            {
                if (uid == null) return false;
                if (api.World.PlayerByUid(uid) == null) return false;
                if (!PlayerSessionStart.ContainsKey(uid)) return false;
                return true;
            }

            // Saves owner and last user of container blocks to disk. This is called automatically by the game engine.
            public override void ToTreeAttributes(ITreeAttribute tree)
            {
                base.ToTreeAttributes(tree); // Tacks on existing behavior.
                if (OwnerUID != null) { tree.SetString("uid", OwnerUID); }
                if (LastUserUID != null) { tree.SetString("lastUserUid", LastUserUID); }

                tree.SetDouble("lastSaveTime", Api.World.Calendar.TotalHours);
            }

            // Fetches owner and last user of conatiner blocks to disk when a chunk is loaded.
            public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
            {
                base.FromTreeAttributes(tree, worldAccessForResolve); // Tacks on existing behavior.
                OwnerUID = tree.GetString("uid");
                LastUserUID = tree.GetString("lastUserUid");

                LastChunkLoadTime = tree.GetDouble("lastSaveTime");
            }
        }
    }
}
