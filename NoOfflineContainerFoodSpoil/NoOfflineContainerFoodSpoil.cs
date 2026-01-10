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
                    behavior.LastChunkLoadTime = api.World.Calendar.TotalHours;

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
            public double? LastUserLoginTime;
            public double LastChunkLoadTime;
            public BEBehaviorOfflinePreserve(BlockEntity entity) : base(entity) { }

            // Updates the block's last user if it doesn't equal the owner, as well as tracks their login time. This is called by the Block BehaviorPreserverWatcher.
            public void UpdateLastUser(IPlayer player)
            {
                if (PlayerSessionStart.TryGetValue(player.PlayerUID, out double currentLoginTime))
                {
                    if (LastUserUID != player.PlayerUID || LastUserLoginTime != currentLoginTime)
                    {
                        LastUserUID = player.PlayerUID;
                        LastUserLoginTime = currentLoginTime;

                        Api.Logger.Notification($"Last User updated to {player.PlayerName} <{player.PlayerUID}> at {Blockentity.Pos}");

                        Blockentity.MarkDirty();
                    }
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
                        string? effectiveUser = OwnerUID; // Default to Owner.

                        if (LastUserUID != null)
                        {
                            if (api.Side == EnumAppSide.Server) // Server time check.
                            {
                                if (NoOfflineContainerFoodSpoilModSystem.PlayerSessionStart.TryGetValue(LastUserUID, out double lastSession))
                                {
                                    if (System.Math.Abs(lastSession - (LastUserLoginTime ?? 0)) < 0.001)
                                    {
                                        effectiveUser = LastUserUID;
                                    }
                                }
                            }
                            else // Client check.
                            {
                                effectiveUser = LastUserUID;
                            }
                        }

                        if (effectiveUser == null || api.World.PlayerByUid(effectiveUser) == null) // Offline check.
                        {
                            return 0f;
                        }

                        if (api.Side == EnumAppSide.Server && PlayerSessionStart.TryGetValue(effectiveUser, out double sessionStart)) // Catch up protection.
                        {
                            if (sessionStart > LastChunkLoadTime) return 0f;
                        }

                        return baseMul;
                    };
                }
            }

            // Saves owner and last user of container blocks to disk. This is called automatically by the game engine.
            public override void ToTreeAttributes(ITreeAttribute tree)
            {
                base.ToTreeAttributes(tree); // Tacks on existing behavior.
                if (OwnerUID != null) { tree.SetString("uid", OwnerUID); }
                if (LastUserUID != null) { tree.SetString("lastUserUid", LastUserUID); }
                if (LastUserLoginTime != null) { tree.SetDouble("lastUserLoginTime", (double)LastUserLoginTime); }

                tree.SetDouble("lastSaveTime", Api.World.Calendar.TotalHours);
            }

            // Fetches owner and last user of conatiner blocks to disk when a chunk is loaded.
            public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
            {
                base.FromTreeAttributes(tree, worldAccessForResolve); // Tacks on existing behavior.
                OwnerUID = tree.GetString("uid");
                LastUserUID = tree.GetString("lastUserUid");
                LastUserLoginTime = tree.GetDouble("lastUserLoginTime"); // Load session
                LastChunkLoadTime = tree.GetDouble("lastSaveTime");
            }
        }
    }
}
