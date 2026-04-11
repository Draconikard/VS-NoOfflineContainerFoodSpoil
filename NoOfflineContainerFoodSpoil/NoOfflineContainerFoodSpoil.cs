using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace NoOfflineContainerFoodSpoil
{
    public class NoOfflineContainerFoodSpoilModSystem : ModSystem
    {
        // Data transfer object to hold offline hours and last time to logout
        public class OfflineHours
        {
            [JsonProperty("total_offline_hours")]
            public double TotalOfflineHours;
            [JsonProperty("last_logout_timestamp")]
            public double LastLogoutTimestamp;
        }

        // Register the BEBehaviorOfflinePreserve behavior
        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockEntityBehaviorClass("OfflinePreserve", typeof(BlockEntityBehaviorOfflinePreserve));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            // Set ownership of a container when placed
            api.Event.DidPlaceBlock += (player, oldId, blockSel, stack) =>
            {
                var blockEntity = api.World.BlockAccessor.GetBlockEntity(blockSel.Position);
                var blockEntityBehavior = blockEntity?.GetBehavior<BlockEntityBehaviorOfflinePreserve>();

                if (blockEntityBehavior != null && blockEntity != null)
                {
                    blockEntityBehavior.OwnerUID = player.PlayerUID;

                    player.ServerData.CustomPlayerData.TryGetValue("NoOfflineFoodSpoil", out string? json);
                    OfflineHours data;
                    try
                    {
                        if (string.IsNullOrEmpty(json)) data = new OfflineHours();
                        else data = JsonConvert.DeserializeObject<OfflineHours>(json) ?? new OfflineHours();
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Error($"[NoOfflineFoodSpoil] Failed to deserialize offline hours for player. Resetting to 0. Error: {ex.Message}");
                        data = new OfflineHours();
                    }
                    blockEntityBehavior.LastKnownOwnerOfflineHours = data.TotalOfflineHours;

                    Mod.Logger.Notification($"Container block placed at {blockEntity?.Pos}. Owner set to {player.PlayerName} <{player.PlayerUID}>.");
                    blockEntity?.MarkDirty(true);
                }
            };

            // Log changes to user's total offline hours
            api.Event.PlayerJoin += byPlayer =>
            {
                var playerOfflineHoursData = byPlayer.ServerData.CustomPlayerData;
                string modKey = "NoOfflineFoodSpoil";

                playerOfflineHoursData.TryGetValue(modKey, out string? json);

                OfflineHours data;
                try
                {
                    if (string.IsNullOrEmpty(json)) data = new OfflineHours();
                    else data = JsonConvert.DeserializeObject<OfflineHours>(json) ?? new OfflineHours();
                }
                catch (Exception ex)
                {
                    api.Logger.Error($"[NoOfflineFoodSpoil] Failed to deserialize offline hours for player. Resetting to 0. Error: {ex.Message}");
                    data = new OfflineHours();
                }

                if (data.LastLogoutTimestamp > 0)
                {
                    double sessionOfflineDuration = Math.Max(0, api.World.Calendar.TotalHours - data.LastLogoutTimestamp);
                    data.TotalOfflineHours += sessionOfflineDuration;
                    data.LastLogoutTimestamp = 0;
                }

                playerOfflineHoursData[modKey] = JsonConvert.SerializeObject(data);
                Mod.Logger.Notification($"[OfflinePreserve] {byPlayer.PlayerName} connected. LastLogout: {data.LastLogoutTimestamp}, TotalOffline: {data.TotalOfflineHours}");
            };

            api.Event.PlayerDisconnect += byPlayer =>
            {
                var playerOfflineHoursData = byPlayer.ServerData.CustomPlayerData;
                string modKey = "NoOfflineFoodSpoil";

                playerOfflineHoursData.TryGetValue(modKey, out string? json);

                OfflineHours data;
                try
                {
                    if (string.IsNullOrEmpty(json)) data = new OfflineHours();
                    else data = JsonConvert.DeserializeObject<OfflineHours>(json) ?? new OfflineHours();
                }
                catch (Exception ex)
                {
                    api.Logger.Error($"[NoOfflineFoodSpoil] Failed to deserialize offline hours for player. Resetting to 0. Error: {ex.Message}");
                    data = new OfflineHours();
                }

                data.LastLogoutTimestamp = api.World.Calendar.TotalHours;
                playerOfflineHoursData[modKey] = JsonConvert.SerializeObject(data);
                Mod.Logger.Notification($"[OfflinePreserve] {byPlayer.PlayerName} disconnected. LastLogout: {data.LastLogoutTimestamp}, TotalOffline: {data.TotalOfflineHours}");
            };
        }

        // Add BlockEntityBehaviorOfflinePreserver behavior to container blocks
        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            foreach (var block in api.World.Blocks) 
            {
                if (block.Code == null || block.EntityClass == null) continue;

                System.Type entityType = api.ClassRegistry.GetBlockEntity(block.EntityClass);

                bool isContainer = entityType != null && typeof(IBlockEntityContainer).IsAssignableFrom(entityType);

                if (!isContainer) continue;

                block.BlockEntityBehaviors = block.BlockEntityBehaviors.Append(new BlockEntityBehaviorType() { Name = "OfflinePreserve" }).ToArray();
            }
        }

        // Behavior assigned to container blocks
        public class BlockEntityBehaviorOfflinePreserve(BlockEntity b) : BlockEntityBehavior(b)
        {
            // Saving OwnerUID to container's attributes
            public string? OwnerUID;
            public double LastKnownOwnerOfflineHours;
            public override void ToTreeAttributes(ITreeAttribute tree)
            {
                base.ToTreeAttributes(tree);
                if (OwnerUID != null) tree.SetString("OwnerUID", OwnerUID);
                tree.SetDouble("LastKnownOwnerOfflineHours", LastKnownOwnerOfflineHours);
            }
            public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessor)
            {
                base.FromTreeAttributes(tree, worldAccessor);
                OwnerUID = tree.GetString("OwnerUID");
                LastKnownOwnerOfflineHours = tree.GetDouble("LastKnownOwnerOfflineHours");
            }

            public override void Initialize(ICoreAPI api, JsonObject properties)
            {
                base.Initialize(api, properties);

                if (b is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
                    container.Inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;

                    api.Logger.Notification($"Initialize has been called on {container.Block.Code}");

                    if (api.Side == EnumAppSide.Server)
                    {
                        ICoreServerAPI? castedAPI = api as ICoreServerAPI;
                        if (castedAPI == null) return;
                        if (OwnerUID == null) return;

                        var ownerProfile = castedAPI.PlayerData.GetPlayerDataByUid(OwnerUID);
                        if (ownerProfile == null) return;

                        // Extract offlineHours from Owner's profile
                        var playerOfflineHoursData = ownerProfile.CustomPlayerData;
                        string modKey = "NoOfflineFoodSpoil";
                        playerOfflineHoursData.TryGetValue(modKey, out string? json);

                        OfflineHours data;
                        try
                        {
                            if (string.IsNullOrEmpty(json)) data = new OfflineHours();
                            else data = JsonConvert.DeserializeObject<OfflineHours>(json) ?? new OfflineHours();
                        } catch (Exception ex)
                        {
                            api.Logger.Error($"[NoOfflineFoodSpoil] Failed to deserialize offline hours for player. Resetting to 0. Error: {ex.Message}");
                            data = new OfflineHours();
                        }

                        double currentTotalOfflineHours = data.TotalOfflineHours;
                        if (data.LastLogoutTimestamp > 0)
                        {
                            currentTotalOfflineHours += Math.Max(0, castedAPI.World.Calendar.TotalHours - data.LastLogoutTimestamp);
                        }

                        double offlineHoursGap = currentTotalOfflineHours - LastKnownOwnerOfflineHours;
                        if (offlineHoursGap > 0)
                        {
                            ApplyForgiveness(container.Inventory, offlineHoursGap);
                            api.Logger.Notification($"[OfflinePreserve] Rewound spoilage by {offlineHoursGap} hours for {container.Block.Code}");
                        }

                        LastKnownOwnerOfflineHours = currentTotalOfflineHours;
                        b.MarkDirty(true);
                    }
                }
            }

            public override void OnBlockRemoved()
            {
                base.OnBlockRemoved();
                if (Blockentity is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
                }
            }

            // Helper functions
            private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float baseMul)
            {
                if (OwnerUID == null || Api?.World?.AllOnlinePlayers == null) return baseMul;

                var owner = Api.World.AllOnlinePlayers.FirstOrDefault(p => p?.PlayerUID == OwnerUID);
                if (owner == null) return 0f;

                if (Api.Side == EnumAppSide.Server)
                {
                    var serverPlayer = owner as IServerPlayer;
                    if (serverPlayer != null && serverPlayer.ConnectionState != EnumClientState.Playing)
                    {
                        return 0f;
                    }
                }

                return baseMul;
            }

            private void ApplyForgiveness(IInventory inventory, double hoursToRewind)
            {

                foreach (var slot in inventory)
                {
                    if (slot.Empty) continue;

                    ITreeAttribute? attr = slot.Itemstack.Attributes.GetTreeAttribute("transitionableDictionary");
                    if (attr == null) continue;

                    foreach (var val in attr)
                    {
                        if (val.Value is ITreeAttribute transProps)
                        {
                            double lastUpdated = transProps.GetDouble("lastUpdated");
                            double currentTime = Api.World.Calendar.TotalHours;
                            double newLastUpdated = Math.Min(currentTime, lastUpdated + hoursToRewind);
                            transProps.SetDouble("lastUpdated", newLastUpdated);

                            slot.MarkDirty();
                        }
                    }
                }
            }
        }
    }
}