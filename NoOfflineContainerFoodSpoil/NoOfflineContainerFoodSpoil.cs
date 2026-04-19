using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
        // Dictionary stores who is online, as well as what containers are loaded into memory.
        public Dictionary<string, IServerPlayer> OnlinePlayers = new Dictionary<string, IServerPlayer>();
        public HashSet<BlockEntityBehaviorOfflinePreserve> LoadedContainers = new();

        // A data structure that is used to track how long a player has been offline. This is attached to each player's custom attributes in players.json
        public class OfflineHours
        {
            [JsonProperty("total_offline_hours")]
            public double TotalOfflineHours;
            [JsonProperty("last_logout_timestamp")]
            public double LastLogoutTimestamp;
        }

        // Registers the custom Block Entity Behavior class on both the client and server when the world is started.
        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockEntityBehaviorClass("OfflinePreserve", typeof(BlockEntityBehaviorOfflinePreserve));
        }

        // Server-side only start logic
        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            api.Event.DidPlaceBlock += (player, oldId, blockSel, stack) =>
            {
                var blockEntity = api.World.BlockAccessor.GetBlockEntity(blockSel.Position);
                var blockEntityBehavior = blockEntity?.GetBehavior<BlockEntityBehaviorOfflinePreserve>();

                if (blockEntityBehavior != null && blockEntity != null)
                {
                    blockEntityBehavior.OwnerUID = player.PlayerUID; // Set the owner of the block to whoever placed it.

                    // Logic called to fetch the player's offline hours and to deserialize the data.
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
                    blockEntityBehavior.LastKnownOwnerOfflineHours = data.TotalOfflineHours; // Stores last known owner's "offlinehours" in the block when it is placed. 

                    Mod.Logger.Notification($"Container block placed at {blockEntity?.Pos}. Owner set to {player.PlayerName} <{player.PlayerUID}>.");
                    blockEntity?.MarkDirty(true);
                }
            };

            api.Event.PlayerJoin += byPlayer =>
            {
                var playerOfflineHoursData = byPlayer.ServerData.CustomPlayerData;
                string modKey = "NoOfflineFoodSpoil";

                playerOfflineHoursData.TryGetValue(modKey, out string? json);

                // Logic called to fetch the player's offline hours and to deserialize the data.
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
                    data.TotalOfflineHours += sessionOfflineDuration; // Adds however long the player has been offline to their offlinehours counter.
                    data.LastLogoutTimestamp = 0;
                }

                playerOfflineHoursData[modKey] = JsonConvert.SerializeObject(data);

                OnlinePlayers[byPlayer.PlayerUID] = byPlayer as IServerPlayer;

                // Saves the last known owner's "offlinehours" to all of the containers owned by them and loaded into memory.
                foreach (var container in LoadedContainers.Where(c => c.OwnerUID == byPlayer.PlayerUID))
                {
                    container.Blockentity?.MarkDirty(true);

                    if (container.Blockentity is BlockEntityContainer bec)
                    {
                        foreach (var slot in bec.Inventory)
                        {
                            slot.MarkDirty();
                        }
                    }
                }

                Mod.Logger.Notification($"[OfflinePreserve] {byPlayer.PlayerName} connected. LastLogout: {data.LastLogoutTimestamp}, TotalOffline: {data.TotalOfflineHours}");
            };

            api.Event.PlayerDisconnect += byPlayer =>
            {
                var playerOfflineHoursData = byPlayer.ServerData.CustomPlayerData;
                string modKey = "NoOfflineFoodSpoil";

                playerOfflineHoursData.TryGetValue(modKey, out string? json);
                // Logic called to fetch the player's offline hours and to deserialize the data. I should use DRY here but honestly I am so done at this point and I don't want to retest for the 40th time after moving this to it's own function.
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

                OnlinePlayers.Remove(byPlayer.PlayerUID);

                // Saves the last known owner's "offlinehours" to all of the containers owned by them and loaded into memory.
                foreach (var container in LoadedContainers.Where(c => c.OwnerUID == byPlayer.PlayerUID))
                {
                    container.Blockentity?.MarkDirty(true);

                    if (container.Blockentity is BlockEntityContainer bec)
                    {
                        foreach (var slot in bec.Inventory)
                        {
                            slot.MarkDirty();
                        }
                    }
                }

                Mod.Logger.Notification($"[OfflinePreserve] {byPlayer.PlayerName} disconnected. LastLogout: {data.LastLogoutTimestamp}, TotalOffline: {data.TotalOfflineHours}");
            };
        }


        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            // Attaches the custom Block Entity Behavior to each block that is recognized as a container with an inventory.
            foreach (var block in api.World.Blocks) 
            {
                if (block.Code == null || block.EntityClass == null) continue;

                System.Type entityType = api.ClassRegistry.GetBlockEntity(block.EntityClass);

                bool isContainer = entityType != null && typeof(IBlockEntityContainer).IsAssignableFrom(entityType);

                if (!isContainer) continue;

                block.BlockEntityBehaviors = block.BlockEntityBehaviors.Append(new BlockEntityBehaviorType() { Name = "OfflinePreserve" }).ToArray();
            }
        }

        public class BlockEntityBehaviorOfflinePreserve(BlockEntity b) : BlockEntityBehavior(b)
        {
            public string? OwnerUID;
            public string? VisitorUID;
            public double LastKnownOwnerOfflineHours;
            public double LastKnownVisitorOfflineHours;
            public double LastKnownCalendarTime;
            public double VisitorStartTime;
            private NoOfflineContainerFoodSpoilModSystem? modSys;

            // Saves and loads variables that the block stores to disk when it loads and unloads.
            public override void ToTreeAttributes(ITreeAttribute tree)
            {
                base.ToTreeAttributes(tree);
                if (OwnerUID != null) tree.SetString("OwnerUID", OwnerUID);
                if (VisitorUID != null) tree.SetString("VisitorUID", VisitorUID);
                tree.SetDouble("LastKnownOwnerOfflineHours", LastKnownOwnerOfflineHours);
                tree.SetDouble("LastKnownVisitorOfflineHours", LastKnownVisitorOfflineHours);
                tree.SetDouble("LastKnownCalendarTime", LastKnownCalendarTime);
                tree.SetDouble("VisitorStartTime", VisitorStartTime);
            }
            public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessor)
            {
                base.FromTreeAttributes(tree, worldAccessor);
                OwnerUID = tree.GetString("OwnerUID");
                VisitorUID = tree.GetString("VisitorUID");
                LastKnownOwnerOfflineHours = tree.GetDouble("LastKnownOwnerOfflineHours");
                LastKnownVisitorOfflineHours = tree.GetDouble("LastKnownVisitorOfflineHours");
                LastKnownCalendarTime = tree.GetDouble("LastKnownCalendarTime");
                VisitorStartTime = tree.GetDouble("VisitorStartTime");
            }

            // Called when chunk that container is in is loaded.
            public override void Initialize(ICoreAPI api, JsonObject properties)
            {
                base.Initialize(api, properties);

                if (b is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
                    container.Inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;

                    container.Inventory.OnInventoryOpened -= OnInventoryOpened;
                    container.Inventory.OnInventoryOpened += OnInventoryOpened;

                    api.Logger.Notification($"Initialize has been called on {container.Block.Code}");

                    if (api.Side == EnumAppSide.Server)
                    {
                        modSys = api.ModLoader.GetModSystem<NoOfflineContainerFoodSpoilModSystem>();
                        modSys.LoadedContainers.Add(this);

                        ICoreServerAPI? castedAPI = api as ICoreServerAPI;
                        if (castedAPI == null || OwnerUID == null) return;

                        var ownerProfile = castedAPI.PlayerData.GetPlayerDataByUid(OwnerUID);
                        if (ownerProfile == null) return;

                        ownerProfile.CustomPlayerData.TryGetValue("NoOfflineFoodSpoil", out string? json);
                        // Logic called to fetch the player's offline hours and to deserialize the data.
                        OfflineHours data;
                        try
                        {
                            data = string.IsNullOrEmpty(json) ? new OfflineHours() : JsonConvert.DeserializeObject<OfflineHours>(json) ?? new OfflineHours();
                        }
                        catch
                        {
                            data = new OfflineHours();
                        }

                        double currentTotalOfflineHours = data.TotalOfflineHours;
                        if (data.LastLogoutTimestamp > 0)
                        {
                            currentTotalOfflineHours += Math.Max(0, castedAPI.World.Calendar.TotalHours - data.LastLogoutTimestamp);
                        }

                        double offlineHoursGap = currentTotalOfflineHours - LastKnownOwnerOfflineHours; // Checks how much time has passed while the owner has been offline.

                        if (VisitorUID != null && offlineHoursGap > 0 && VisitorStartTime > 0)
                        {
                            var visitorProfile = castedAPI.PlayerData.GetPlayerDataByUid(VisitorUID);
                            if (visitorProfile != null)
                            {
                                visitorProfile.CustomPlayerData.TryGetValue("NoOfflineFoodSpoil", out string? vJson);
                                var vData = string.IsNullOrEmpty(vJson) ? new OfflineHours() : JsonConvert.DeserializeObject<OfflineHours>(vJson) ?? new OfflineHours();

                                double currentVisitorOfflineHours = vData.TotalOfflineHours;
                                if (vData.LastLogoutTimestamp > 0)
                                {
                                    currentVisitorOfflineHours += Math.Max(0, castedAPI.World.Calendar.TotalHours - vData.LastLogoutTimestamp);
                                }

                                double visitorOfflineGap = currentVisitorOfflineHours - LastKnownVisitorOfflineHours;
                                double timeElapsedSinceVisitor = castedAPI.World.Calendar.TotalHours - VisitorStartTime;
                                double visitorOnlineTime = Math.Max(0, timeElapsedSinceVisitor - visitorOfflineGap);

                                offlineHoursGap = Math.Max(0, offlineHoursGap - visitorOnlineTime);
                            }

                            VisitorUID = null;
                            VisitorStartTime = 0;
                        }

                        if (offlineHoursGap > 0)
                        {
                            ApplyForgiveness(container.Inventory, offlineHoursGap); // Applies forgivenes to each item in the chest based on how long the owner has been offline.
                            api.Logger.Notification($"[OfflinePreserve] Rewound spoilage by {offlineHoursGap} hours for {container.Block.Code}");
                        }

                        LastKnownOwnerOfflineHours = currentTotalOfflineHours;
                        LastKnownCalendarTime = castedAPI.World.Calendar.TotalHours;
                        b.MarkDirty(true);
                    }
                }
            }

            // Cleans up subscriptions to events.
            public override void OnBlockRemoved()
            {
                base.OnBlockRemoved();
                if (Blockentity is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
                    container.Inventory.OnInventoryOpened -= OnInventoryOpened;
                }
                RemoveFromRegistry();
            }

            // Cleans up subscriptions to events.
            public override void OnBlockUnloaded()
            {
                base.OnBlockUnloaded();
                if (Blockentity is BlockEntityContainer container)
                {
                    container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
                    container.Inventory.OnInventoryOpened -= OnInventoryOpened;
                }
                RemoveFromRegistry();
            }

            // Sets the spoilage to zero for a chest if an owner is offline and a visitor doesn't exist.
            private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float baseMul)
            {
                if (OwnerUID == null || Api == null) return baseMul;

                if (Api.Side == EnumAppSide.Server)
                {
                    if (modSys == null) return baseMul;

                    bool isOwnerOnline = modSys.OnlinePlayers.TryGetValue(OwnerUID, out var ownerPlayer)
                             && ownerPlayer?.ConnectionState == EnumClientState.Playing;

                    bool isVisitorOnline = VisitorUID != null
                            && modSys.OnlinePlayers.TryGetValue(VisitorUID, out var visitorPlayer)
                            && visitorPlayer?.ConnectionState == EnumClientState.Playing;

                    if (isOwnerOnline || isVisitorOnline)
                    {
                        return baseMul;
                    }

                    return 0f;
                }
                else
                {
                    bool isOwnerOnlineClient = Api.World.AllOnlinePlayers.Any(p => p?.PlayerUID == OwnerUID);
                    bool isVisitorOnlineClient = VisitorUID != null && Api.World.AllOnlinePlayers.Any(p => p?.PlayerUID == VisitorUID);

                    if (isOwnerOnlineClient || isVisitorOnlineClient)
                    {
                        return baseMul;
                    }

                    return 0f;
                }
            }

            // Sets a visitor when a container is opened.
            private void OnInventoryOpened(IPlayer player)
            {
                if (Api.Side != EnumAppSide.Server) return;

                if (player.PlayerUID == OwnerUID)
                {
                    VisitorUID = null;
                    VisitorStartTime = 0;
                }
                else
                {
                    VisitorUID = player.PlayerUID;
                    VisitorStartTime = Api.World.Calendar.TotalHours;

                    var profile = (Api as ICoreServerAPI)?.PlayerData.GetPlayerDataByUid(player.PlayerUID);
                    if (profile == null) return;

                    profile.CustomPlayerData.TryGetValue("NoOfflineFoodSpoil", out string? json);
                    OfflineHours data;
                    try
                    {
                        data = string.IsNullOrEmpty(json) ? new OfflineHours() : JsonConvert.DeserializeObject<OfflineHours>(json) ?? new OfflineHours();
                        LastKnownVisitorOfflineHours = data.TotalOfflineHours;
                    }
                    catch
                    {
                        data = new OfflineHours();
                    }
                }

                Blockentity.MarkDirty(true);
            }

            // Helper method for performing "forgiveness" to each item in a container.
            private void ApplyForgiveness(IInventory inventory, double hoursToRewind)
            {
                foreach (var slot in inventory)
                {
                    if (slot.Empty) continue;

                    ITreeAttribute? attr = slot.Itemstack.Attributes.GetTreeAttribute("transitionstate");
                    if (attr == null) continue;

                    double lastUpdated = attr.GetDouble("lastUpdatedTotalHours");
                    if (lastUpdated > 0)
                    {
                        double currentTime = Api.World.Calendar.TotalHours;
                        double newLastUpdated = Math.Min(currentTime, lastUpdated + hoursToRewind);

                        attr.SetDouble("lastUpdatedTotalHours", newLastUpdated);

                        double created = attr.GetDouble("createdTotalHours");
                        if (created > 0)
                        {
                            attr.SetDouble("createdTotalHours", Math.Min(Api.World.Calendar.TotalHours, created + hoursToRewind));
                        }

                        slot.Itemstack.TempAttributes.RemoveAttribute("transitionState");
                        slot.MarkDirty();
                    }
                }
            }

            // Helper method to remove a block from the "Loaded Containers" list.
            private void RemoveFromRegistry()
            {
                if (Api?.Side == EnumAppSide.Server && modSys != null)
                {
                    modSys.LoadedContainers.Remove(this);
                }
            }
        }
    }
}