// ============================================================================
// Moo — Invisible Boombox Plugin for Rust (Oxide/uMod)
// Author: Viktor AI  |  Requested by Dionysus & partner admin
// ============================================================================
// A portable invisible boombox that attaches to the admin player and plays
// Doja Cat - Mooo! (or any configured audio URL). Other players hear the
// music emanating from the admin's position but cannot see the boombox.
//
// Setup:
//   1. Drop this file into oxide/plugins/
//   2. Add audio URL to server config:
//        BoomBox.ServerUrlList "Mooo,https://raw.githubusercontent.com/HeyImDionysus/moo/main/mooo.mp3"
//   3. Grant permission:  oxide.grant user <steamid> moo.use
//
// Commands:
//   /moo          — Attach the invisible boombox and start playing
//   /moo.stop     — Stop playback (boombox stays attached)
//   /moo.play     — Resume playback
//   /moo.vol 0-10 — Set volume level (0 = silent, 10 = max)
//   /moo.remove   — Remove the boombox entirely
//   /moo.stealth  — Toggle stealth mode (hides audio from all other players)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Network;

namespace Oxide.Plugins
{
    [Info("Moo", "Viktor", "1.0.0")]
    [Description("Invisible portable boombox that follows the player and plays audio")]
    class Moo : RustPlugin
    {
        #region Constants

        private const string PERM_USE = "moo.use";
        private const string BOOMBOX_PREFAB = "assets/prefabs/voiceaudio/boombox/boombox.deployed.prefab";

        #endregion

        #region Configuration

        private PluginConfig config;

        private class PluginConfig
        {
            [JsonProperty("Audio URL (direct .mp3 link)")]
            public string AudioUrl = "https://raw.githubusercontent.com/HeyImDionysus/moo/main/mooo.mp3";

            [JsonProperty("Default Volume (0.0 - 1.0)")]
            public float DefaultVolume = 1.0f;

            [JsonProperty("Auto-Play On Attach")]
            public bool AutoPlay = true;

            [JsonProperty("Boombox Local Offset (x,y,z from player)")]
            public string BoomboxOffset = "0,1.0,0";

            [JsonProperty("Stealth Mode Default (hide from all players)")]
            public bool StealthDefault = false;

            [JsonProperty("Follow Update Interval (seconds)")]
            public float FollowInterval = 0.1f;
        }

        protected override void LoadDefaultConfig() => config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { config = Config.ReadObject<PluginConfig>(); }
            catch { PrintWarning("Invalid config, loading defaults."); config = new PluginConfig(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Data Structures

        private class MooData
        {
            public BaseEntity boomboxEntity;
            public ulong ownerID;
            public bool isPlaying;
            public bool isStealth;
            public float volume;
            public Timer followTimer;

            public void Cleanup()
            {
                followTimer?.Destroy();
                followTimer = null;
            }
        }

        #endregion

        #region Fields

        // Owner steamID → boombox data
        private readonly Dictionary<ulong, MooData> activeMoos = new Dictionary<ulong, MooData>();

        // Entity net ID → moo data (for CanNetworkTo)
        private readonly Dictionary<NetworkableId, MooData> entityLookup = new Dictionary<NetworkableId, MooData>();

        // Reflection for BoomBox URL
        private FieldInfo _currentRadioIpField;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);

            // Cache reflection for BoomBox URL control
            _currentRadioIpField = typeof(BoomBox).GetField("CurrentRadioIp",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_currentRadioIpField == null)
            {
                _currentRadioIpField = typeof(BoomBox).GetField("<CurrentRadioIp>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
        }

        private void Unload()
        {
            foreach (var kvp in activeMoos.ToList())
                DestroyMoo(kvp.Value);
            activeMoos.Clear();
            entityLookup.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            if (activeMoos.TryGetValue(player.userID, out var data))
            {
                DestroyMoo(data);
                activeMoos.Remove(player.userID);
            }
        }

        #endregion

        #region Chat Commands

        [ChatCommand("moo")]
        private void CmdMoo(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                player.ChatMessage("<color=#ff4444>You don't have permission to use Moo!</color>");
                return;
            }

            // If already has one, inform them
            if (activeMoos.TryGetValue(player.userID, out var existing))
            {
                if (existing.boomboxEntity != null && !existing.boomboxEntity.IsDestroyed)
                {
                    // Toggle play/stop instead
                    if (existing.isPlaying)
                    {
                        StopAudio(existing);
                        player.ChatMessage("<color=#FFD700>🐄 Moo paused! Use /moo.play to resume or /moo.remove to remove.</color>");
                    }
                    else
                    {
                        StartAudio(existing);
                        player.ChatMessage("<color=#FFD700>🐄 Moo resumed!</color>");
                    }
                    return;
                }
                // Old one destroyed, clean up
                DestroyMoo(existing);
                activeMoos.Remove(player.userID);
            }

            // Spawn new boombox
            SpawnMoo(player);
        }

        [ChatCommand("moo.play")]
        private void CmdMooPlay(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            StartAudio(data);
            player.ChatMessage("<color=#FFD700>🐄 Mooo! Playing...</color>");
        }

        [ChatCommand("moo.stop")]
        private void CmdMooStop(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            StopAudio(data);
            player.ChatMessage("<color=#FFD700>🐄 Moo stopped.</color>");
        }

        [ChatCommand("moo.vol")]
        private void CmdMooVolume(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            if (args.Length == 0)
            {
                player.ChatMessage($"<color=#FFD700>🐄 Current volume: {data.volume * 10:F0}/10. Usage: /moo.vol 0-10</color>");
                return;
            }

            if (!float.TryParse(args[0], out float level) || level < 0 || level > 10)
            {
                player.ChatMessage("<color=#ff4444>Volume must be a number from 0 to 10.</color>");
                return;
            }

            data.volume = level / 10f;
            ApplyVolume(data);
            player.ChatMessage($"<color=#FFD700>🐄 Volume set to {level:F0}/10</color>");
        }

        [ChatCommand("moo.stealth")]
        private void CmdMooStealth(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            data.isStealth = !data.isStealth;

            if (data.boomboxEntity != null)
                data.boomboxEntity.SendNetworkUpdateImmediate();

            string state = data.isStealth ? "ON — only you can hear it" : "OFF — everyone nearby can hear it";
            player.ChatMessage($"<color=#FFD700>🐄 Stealth mode: {state}</color>");
        }

        [ChatCommand("moo.remove")]
        private void CmdMooRemove(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
                return;

            if (activeMoos.TryGetValue(player.userID, out var data))
            {
                DestroyMoo(data);
                activeMoos.Remove(player.userID);
                player.ChatMessage("<color=#FFD700>🐄 Moo removed!</color>");
            }
            else
            {
                player.ChatMessage("<color=#ff4444>You don't have an active Moo boombox.</color>");
            }
        }

        #endregion

        #region Spawn / Destroy

        private void SpawnMoo(BasePlayer player)
        {
            var offset = ParseVector3(config.BoomboxOffset, new Vector3(0f, 1.0f, 0f));
            var pos = player.transform.position + offset;

            // Spawn boombox entity
            var boomboxEntity = GameManager.server.CreateEntity(BOOMBOX_PREFAB, pos);
            if (boomboxEntity == null)
            {
                player.ChatMessage("<color=#ff4444>Failed to spawn boombox. Check server logs.</color>");
                return;
            }

            boomboxEntity.OwnerID = player.userID;

            // Parent to the player so it follows them automatically
            boomboxEntity.SetParent(player, "");
            boomboxEntity.transform.localPosition = offset;
            boomboxEntity.transform.localRotation = Quaternion.identity;

            boomboxEntity.Spawn();

            // Disable ground checks so it doesn't destroy itself
            var gw = boomboxEntity.GetComponent<GroundWatch>();
            if (gw != null) UnityEngine.Object.Destroy(gw);
            var dgm = boomboxEntity.GetComponent<DestroyOnGroundMissing>();
            if (dgm != null) UnityEngine.Object.Destroy(dgm);

            // Make invulnerable
            var combat = boomboxEntity as BaseCombatEntity;
            if (combat != null)
                combat.SetHealth(combat.MaxHealth());

            var data = new MooData
            {
                boomboxEntity = boomboxEntity,
                ownerID = player.userID,
                isPlaying = false,
                isStealth = config.StealthDefault,
                volume = config.DefaultVolume
            };

            // Register for CanNetworkTo
            if (boomboxEntity.net != null)
                entityLookup[boomboxEntity.net.ID] = data;

            activeMoos[player.userID] = data;

            // Power the boombox — set the power flag so it can play
            NextTick(() =>
            {
                if (boomboxEntity == null || boomboxEntity.IsDestroyed) return;

                // Set Reserved8 flag = "has power" for IOEntity-based entities
                boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

                // Also ensure any IOEntity power state is satisfied
                var ioEntity = boomboxEntity as IOEntity;
                if (ioEntity != null)
                {
                    ioEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    ioEntity.SendNetworkUpdateImmediate();
                }

                // Apply volume
                ApplyVolume(data);

                // Auto-play if configured
                if (config.AutoPlay)
                {
                    StartAudio(data);
                    player.ChatMessage("<size=18><color=#FFD700>🐄 \"Mooo!\" — Boombox attached and playing!</color></size>");
                    player.ChatMessage("<color=#aaaaaa>Commands: /moo (toggle), /moo.stop, /moo.play, /moo.vol 0-10, /moo.stealth, /moo.remove</color>");
                }
                else
                {
                    player.ChatMessage("<size=18><color=#FFD700>🐄 Moo boombox attached!</color></size>");
                    player.ChatMessage("<color=#aaaaaa>Use /moo.play to start. Commands: /moo.stop, /moo.vol 0-10, /moo.stealth, /moo.remove</color>");
                }

                Puts($"[Moo] {player.displayName} attached a Moo boombox");
            });

            // Start a follow timer as fallback (in case parenting doesn't stick on respawn)
            data.followTimer = timer.Every(config.FollowInterval, () =>
            {
                if (boomboxEntity == null || boomboxEntity.IsDestroyed)
                {
                    data.Cleanup();
                    activeMoos.Remove(player.userID);
                    return;
                }

                // Re-parent if detached
                if (boomboxEntity.GetParentEntity() == null || boomboxEntity.GetParentEntity() != player)
                {
                    if (player != null && player.IsConnected && !player.IsDead())
                    {
                        boomboxEntity.SetParent(player, "");
                        boomboxEntity.transform.localPosition = offset;
                        boomboxEntity.transform.localRotation = Quaternion.identity;
                        boomboxEntity.SendNetworkUpdateImmediate();
                    }
                }

                // Keep power on
                if (!boomboxEntity.HasFlag(BaseEntity.Flags.Reserved8))
                    boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

                // Keep health full
                var cb = boomboxEntity as BaseCombatEntity;
                if (cb != null && cb.health < cb.MaxHealth())
                    cb.SetHealth(cb.MaxHealth());
            });
        }

        private void DestroyMoo(MooData data)
        {
            data.Cleanup();

            // Unregister from lookup
            if (data.boomboxEntity?.net != null)
                entityLookup.Remove(data.boomboxEntity.net.ID);

            // Kill the entity
            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
                data.boomboxEntity.Kill();
        }

        #endregion

        #region Visibility Hook

        private object CanNetworkTo(BaseNetworkable entity, BasePlayer player)
        {
            if (entity?.net == null || player == null) return null;

            // Fast exit for non-moo entities
            if (!entityLookup.TryGetValue(entity.net.ID, out var data)) return null;

            // Owner always sees it (well, it's invisible anyway but they "have" it)
            if (player.userID == data.ownerID) return null;

            // In stealth mode, hide from everyone (audio won't play for them either)
            if (data.isStealth) return false;

            // Normal mode: entity is networked to nearby players (they hear audio)
            // but visually the boombox is hidden inside the player model
            // We let it network so others can hear the audio
            return null;
        }

        #endregion

        #region Hooks — Protection

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return true; // Block all damage to moo boomboxes
            return null;
        }

        private object OnEntityDecay(BaseCombatEntity entity)
        {
            if (entity?.net == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return true; // Prevent decay
            return null;
        }

        // Prevent other players from picking up or interacting with the boombox
        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net == null || player == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return false; // Block pickup
            return null;
        }

        // Prevent looting the boombox
        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net == null || player == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return false;
            return null;
        }

        #endregion

        #region Hooks — Player Death / Respawn

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            if (!activeMoos.TryGetValue(player.userID, out var data)) return;

            // Stop audio on death but keep the data
            StopAudio(data);

            // Detach boombox temporarily
            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
            {
                data.boomboxEntity.SetParent(null);
                // Move it underground temporarily so it's out of the way
                data.boomboxEntity.transform.position = new Vector3(0, -500f, 0);
                data.boomboxEntity.SendNetworkUpdateImmediate();
            }
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            if (!activeMoos.TryGetValue(player.userID, out var data)) return;

            // Re-attach boombox after respawn
            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
            {
                var offset = ParseVector3(config.BoomboxOffset, new Vector3(0f, 1.0f, 0f));

                timer.Once(1f, () =>
                {
                    if (player == null || !player.IsConnected) return;
                    if (data.boomboxEntity == null || data.boomboxEntity.IsDestroyed) return;

                    data.boomboxEntity.SetParent(player, "");
                    data.boomboxEntity.transform.localPosition = offset;
                    data.boomboxEntity.transform.localRotation = Quaternion.identity;
                    data.boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    data.boomboxEntity.SendNetworkUpdateImmediate();

                    if (data.isPlaying)
                        StartAudio(data);

                    player.ChatMessage("<color=#FFD700>🐄 Moo boombox re-attached!</color>");
                });
            }
        }

        #endregion

        #region Audio System

        private void StartAudio(MooData data)
        {
            if (data.boomboxEntity == null || data.boomboxEntity.IsDestroyed) return;

            var deployable = data.boomboxEntity as DeployableBoomBox;
            if (deployable == null) return;

            try
            {
                var controller = deployable.BoxController;
                if (controller == null)
                {
                    PrintWarning("[Moo] BoomBox controller is null");
                    return;
                }

                // Ensure power flag is set
                data.boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

                // Set the URL
                SetBoomboxUrl(controller, config.AudioUrl);

                // Start playback
                controller.ServerTogglePlay(true);
                data.isPlaying = true;
            }
            catch (Exception ex)
            {
                PrintWarning($"[Moo] Failed to start audio: {ex.Message}");
            }
        }

        private void StopAudio(MooData data)
        {
            if (data.boomboxEntity == null || data.boomboxEntity.IsDestroyed) return;

            var deployable = data.boomboxEntity as DeployableBoomBox;
            if (deployable == null) return;

            try
            {
                var controller = deployable.BoxController;
                if (controller == null) return;

                controller.ServerTogglePlay(false);
                data.isPlaying = false;
            }
            catch (Exception ex)
            {
                PrintWarning($"[Moo] Failed to stop audio: {ex.Message}");
            }
        }

        private void ApplyVolume(MooData data)
        {
            if (data.boomboxEntity == null || data.boomboxEntity.IsDestroyed) return;

            var deployable = data.boomboxEntity as DeployableBoomBox;
            if (deployable == null) return;

            try
            {
                var controller = deployable.BoxController;
                if (controller == null) return;

                // BoomBox has a HeldBoomBox parent with volume, but for deployed
                // boombox, we adjust the audio source radius via the controller.
                // The `BaseVolumeLevel` field controls volume in newer Rust.
                var volumeField = typeof(BoomBox).GetField("BaseVolumeLevel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (volumeField != null)
                {
                    volumeField.SetValue(controller, data.volume);
                }

                // Also try the newer property approach
                var volumeProp = typeof(BoomBox).GetProperty("CurrentVolume",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (volumeProp != null && volumeProp.CanWrite)
                {
                    volumeProp.SetValue(controller, data.volume);
                }

                controller.SendNetworkUpdate();
            }
            catch (Exception ex)
            {
                PrintWarning($"[Moo] Failed to set volume: {ex.Message}");
            }
        }

        private void SetBoomboxUrl(BoomBox controller, string url)
        {
            if (_currentRadioIpField != null)
            {
                _currentRadioIpField.SetValue(controller, url);
            }

            // Notify clients of URL change
            controller.baseEntity?.ClientRPC<string>(null, "OnRadioIPChanged", url);
        }

        #endregion

        #region Helpers

        private bool HasMoo(BasePlayer player, out MooData data)
        {
            data = null;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                player.ChatMessage("<color=#ff4444>You don't have permission to use Moo!</color>");
                return false;
            }

            if (!activeMoos.TryGetValue(player.userID, out data) ||
                data.boomboxEntity == null || data.boomboxEntity.IsDestroyed)
            {
                player.ChatMessage("<color=#ff4444>You don't have an active Moo boombox. Use /moo to attach one.</color>");
                return false;
            }

            return true;
        }

        private Vector3 ParseVector3(string input, Vector3 fallback)
        {
            try
            {
                var parts = input.Split(',');
                if (parts.Length == 3)
                    return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
            }
            catch { }
            return fallback;
        }

        #endregion
    }
}
