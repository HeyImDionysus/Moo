// ============================================================================
// Moo — Invisible Boombox Plugin for Rust (Oxide/uMod)
// Plays Doja Cat - Mooo! from an invisible boombox attached to the player.
// Everyone nearby hears the music.
// ============================================================================
// Setup:
//   1. Drop this file into oxide/plugins/
//   2. Add audio URL to server config:
//        BoomBox.ServerUrlList "Mooo,https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3"
//   3. Grant permission:  oxide.grant user <steamid> moo.use
//
// Commands:
//   /moo          — Attach the invisible boombox and start playing
//   /moo.play     — Resume playback
//   /moo.stop     — Pause playback (boombox stays attached)
//   /moo.vol 0-10 — Set volume level (0 = silent, 10 = max)
//   /moo.stealth  — Toggle stealth mode (only you can hear it)
//   /moo.remove   — Remove the boombox entirely
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Moo", "Viktor", "1.2.0")]
    [Description("Invisible portable boombox that plays Doja Cat - Mooo!")]
    class Moo : RustPlugin
    {
        private const string PERM_USE = "moo.use";
        private const string BOOMBOX_PREFAB = "assets/prefabs/voiceaudio/boombox/boombox.deployed.prefab";
        private const string AUDIO_URL = "https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3";

        // Parent to "spine1" bone so the boombox sits inside the player's
        // torso where the body model fully occludes it.  Fallback: if the
        // bone doesn't resolve, Rust parents to root at Vector3.zero (feet
        // level, partially underground) — still far better than above-head.
        private const string PARENT_BONE = "spine1";
        private static readonly Vector3 BOOMBOX_LOCAL_POS = Vector3.zero;

        #region Data

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

        // Owner steamID → boombox data
        private readonly Dictionary<ulong, MooData> activeMoos = new Dictionary<ulong, MooData>();

        // Net IDs of all active moo boomboxes — used by protection hooks
        // so they only protect actual boombox entities, not all entities
        // owned by the same player.
        private readonly HashSet<uint> _mooNetIds = new HashSet<uint>();

        // Net IDs of boomboxes currently in stealth mode — lets
        // CanNetworkTo fast-exit for every other entity in the game.
        private readonly HashSet<uint> _stealthNetIds = new HashSet<uint>();

        // Reflection for BoomBox URL
        private FieldInfo _currentRadioIpField;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);

            _currentRadioIpField = typeof(BoomBox).GetField("CurrentRadioIp",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_currentRadioIpField == null)
            {
                _currentRadioIpField = typeof(BoomBox).GetField("<CurrentRadioIp>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            // Start with ALL heavy hooks unsubscribed.
            // They are subscribed on-demand when a moo spawns / stealth activates.
            // CanNetworkTo is the most expensive — called for every entity on
            // every network tick. Keeping it unsubscribed when no stealth is
            // active eliminates all per-entity overhead from this plugin.
            Unsubscribe(nameof(CanNetworkTo));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnEntityDecay));
            Unsubscribe(nameof(CanPickupEntity));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(OnPlayerDeath));
            Unsubscribe(nameof(OnPlayerRespawned));
            Unsubscribe(nameof(OnPlayerDisconnected));
        }

        private void Unload()
        {
            foreach (var kvp in activeMoos.ToList())
                DestroyMoo(kvp.Value);
            activeMoos.Clear();
            _mooNetIds.Clear();
            _stealthNetIds.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            if (activeMoos.TryGetValue(player.userID, out var data))
            {
                DestroyMoo(data);
                activeMoos.Remove(player.userID);
                CheckUnsubscribe();
            }
        }

        #endregion

        #region Hook Subscription Management

        /// <summary>
        /// Subscribe to protection + lifecycle hooks when the first moo spawns.
        /// </summary>
        private void SubscribeMooHooks()
        {
            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnEntityDecay));
            Subscribe(nameof(CanPickupEntity));
            Subscribe(nameof(CanLootEntity));
            Subscribe(nameof(OnPlayerDeath));
            Subscribe(nameof(OnPlayerRespawned));
            Subscribe(nameof(OnPlayerDisconnected));
        }

        /// <summary>
        /// Unsubscribe from everything once no moos remain.
        /// </summary>
        private void CheckUnsubscribe()
        {
            if (activeMoos.Count == 0)
            {
                Unsubscribe(nameof(OnEntityTakeDamage));
                Unsubscribe(nameof(OnEntityDecay));
                Unsubscribe(nameof(CanPickupEntity));
                Unsubscribe(nameof(CanLootEntity));
                Unsubscribe(nameof(OnPlayerDeath));
                Unsubscribe(nameof(OnPlayerRespawned));
                Unsubscribe(nameof(OnPlayerDisconnected));
            }
            if (_stealthNetIds.Count == 0)
                Unsubscribe(nameof(CanNetworkTo));
        }

        #endregion

        #region Chat Commands

        [ChatCommand("moo")]
        private void CmdMoo(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                player.ChatMessage("<color=#ff4444>[Moo] You don't have permission!</color>");
                return;
            }

            if (activeMoos.TryGetValue(player.userID, out var existing))
            {
                if (existing.boomboxEntity != null && !existing.boomboxEntity.IsDestroyed)
                {
                    if (existing.isPlaying)
                    {
                        StopAudio(existing);
                        player.ChatMessage("<color=#FFD700>[Moo] Paused! Use /moo.play to resume or /moo.remove to remove.</color>");
                    }
                    else
                    {
                        StartAudio(existing);
                        player.ChatMessage("<color=#FFD700>[Moo] Playing...</color>");
                    }
                    return;
                }
                DestroyMoo(existing);
                activeMoos.Remove(player.userID);
            }

            SpawnMoo(player);
        }

        [ChatCommand("moo.play")]
        private void CmdMooPlay(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;
            StartAudio(data);
            player.ChatMessage("<color=#FFD700>[Moo] Playing...</color>");
        }

        [ChatCommand("moo.stop")]
        private void CmdMooStop(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;
            StopAudio(data);
            player.ChatMessage("<color=#FFD700>[Moo] Stopped.</color>");
        }

        [ChatCommand("moo.vol")]
        private void CmdMooVolume(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            if (args.Length == 0)
            {
                player.ChatMessage($"<color=#FFD700>[Moo] Volume: {data.volume * 10:F0}/10. Usage: /moo.vol 0-10</color>");
                return;
            }

            if (!float.TryParse(args[0], out float level) || level < 0 || level > 10)
            {
                player.ChatMessage("<color=#ff4444>[Moo] Volume must be 0-10.</color>");
                return;
            }

            data.volume = level / 10f;
            ApplyVolume(data);
            player.ChatMessage($"<color=#FFD700>[Moo] Volume set to {level:F0}/10</color>");
        }

        [ChatCommand("moo.stealth")]
        private void CmdMooStealth(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            data.isStealth = !data.isStealth;

            // Manage stealth net-ID tracking + hook subscription
            if (data.boomboxEntity?.net != null)
            {
                uint netId = data.boomboxEntity.net.ID.Value;
                if (data.isStealth)
                {
                    _stealthNetIds.Add(netId);
                    Subscribe(nameof(CanNetworkTo));
                }
                else
                {
                    _stealthNetIds.Remove(netId);
                    if (_stealthNetIds.Count == 0)
                        Unsubscribe(nameof(CanNetworkTo));
                }
            }

            if (data.boomboxEntity != null)
                data.boomboxEntity.SendNetworkUpdateImmediate();

            string state = data.isStealth ? "ON (only you hear it)" : "OFF (everyone hears it)";
            player.ChatMessage($"<color=#FFD700>[Moo] Stealth: {state}</color>");
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
                CheckUnsubscribe();
                player.ChatMessage("<color=#FFD700>[Moo] Removed!</color>");
            }
            else
            {
                player.ChatMessage("<color=#ff4444>[Moo] No active boombox. Use /moo to attach one.</color>");
            }
        }

        #endregion

        #region Spawn / Destroy

        private void SpawnMoo(BasePlayer player)
        {
            var pos = player.transform.position;

            var boomboxEntity = GameManager.server.CreateEntity(BOOMBOX_PREFAB, pos);
            if (boomboxEntity == null)
            {
                player.ChatMessage("<color=#ff4444>[Moo] Failed to spawn boombox.</color>");
                return;
            }

            boomboxEntity.OwnerID = player.userID;
            boomboxEntity.enableSaving = false;

            // Parent to a body bone so the player model hides the boombox
            boomboxEntity.SetParent(player, PARENT_BONE);
            boomboxEntity.transform.localPosition = BOOMBOX_LOCAL_POS;
            boomboxEntity.transform.localRotation = Quaternion.identity;

            boomboxEntity.Spawn();

            // Disable ground checks so it doesn't self-destruct
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
                isStealth = false,
                volume = 1.0f
            };

            if (boomboxEntity.net != null)
                _mooNetIds.Add(boomboxEntity.net.ID.Value);

            activeMoos[player.userID] = data;

            // Subscribe to protection + lifecycle hooks now that a moo exists
            SubscribeMooHooks();

            // Power and auto-play on next tick
            NextTick(() =>
            {
                if (boomboxEntity == null || boomboxEntity.IsDestroyed) return;

                boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

                var ioEntity = boomboxEntity as IOEntity;
                if (ioEntity != null)
                {
                    ioEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    ioEntity.SendNetworkUpdateImmediate();
                }

                StartAudio(data);
                player.ChatMessage("<color=#FFD700>[Moo] Boombox attached and playing!</color>");
                player.ChatMessage("<color=#aaaaaa>Commands: /moo (toggle) | /moo.stop | /moo.play | /moo.vol 0-10 | /moo.stealth | /moo.remove</color>");

                Puts($"[Moo] {player.displayName} attached a Moo boombox");
            });

            // Safety-net timer: re-parent only if detached, keep power on.
            // Runs every 2 s (not 0.1 s) and only sends a network update
            // when something actually changed.
            data.followTimer = timer.Every(2f, () =>
            {
                if (boomboxEntity == null || boomboxEntity.IsDestroyed)
                {
                    data.Cleanup();
                    activeMoos.Remove(player.userID);
                    CheckUnsubscribe();
                    return;
                }

                bool dirty = false;

                // Re-parent only if somehow detached
                var parent = boomboxEntity.GetParentEntity();
                if (parent != player)
                {
                    if (player != null && player.IsConnected && !player.IsDead())
                    {
                        boomboxEntity.SetParent(player, PARENT_BONE);
                        boomboxEntity.transform.localPosition = BOOMBOX_LOCAL_POS;
                        boomboxEntity.transform.localRotation = Quaternion.identity;
                        dirty = true;
                    }
                }

                // Keep power flag on
                if (!boomboxEntity.HasFlag(BaseEntity.Flags.Reserved8))
                {
                    boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    dirty = true;
                }

                // Top up health silently
                var cb = boomboxEntity as BaseCombatEntity;
                if (cb != null && cb.health < cb.MaxHealth())
                    cb.SetHealth(cb.MaxHealth());

                if (dirty)
                    boomboxEntity.SendNetworkUpdateImmediate();
            });
        }

        private void DestroyMoo(MooData data)
        {
            data.Cleanup();

            if (data.boomboxEntity?.net != null)
            {
                uint netId = data.boomboxEntity.net.ID.Value;
                _mooNetIds.Remove(netId);
                _stealthNetIds.Remove(netId);
            }

            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
                data.boomboxEntity.Kill();
        }

        #endregion

        #region Hooks — Visibility (stealth only)

        /// <summary>
        /// Only subscribed when at least one boombox is in stealth mode.
        /// Completely unsubscribed otherwise — zero per-entity overhead.
        /// </summary>
        private object CanNetworkTo(BaseNetworkable entity, BasePlayer player)
        {
            if (entity?.net == null) return null;
            if (!_stealthNetIds.Contains(entity.net.ID.Value)) return null;

            // This IS a stealth boombox — owner keeps it, everyone else is blocked
            var baseEnt = entity as BaseEntity;
            if (baseEnt != null && player.userID == baseEnt.OwnerID)
                return null;
            return false;
        }

        #endregion

        #region Hooks — Protection

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net != null && _mooNetIds.Contains(entity.net.ID.Value))
                return true;
            return null;
        }

        private object OnEntityDecay(BaseCombatEntity entity)
        {
            if (entity?.net != null && _mooNetIds.Contains(entity.net.ID.Value))
                return true;
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net != null && _mooNetIds.Contains(entity.net.ID.Value))
                return false;
            return null;
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net != null && _mooNetIds.Contains(entity.net.ID.Value))
                return false;
            return null;
        }

        #endregion

        #region Hooks — Death / Respawn

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            if (!activeMoos.TryGetValue(player.userID, out var data)) return;

            StopAudio(data);
            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
            {
                data.boomboxEntity.SetParent(null);
                data.boomboxEntity.transform.position = new Vector3(0, -500f, 0);
                data.boomboxEntity.SendNetworkUpdateImmediate();
            }
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            if (!activeMoos.TryGetValue(player.userID, out var data)) return;

            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
            {
                timer.Once(1f, () =>
                {
                    if (player == null || !player.IsConnected) return;
                    if (data.boomboxEntity == null || data.boomboxEntity.IsDestroyed) return;

                    data.boomboxEntity.SetParent(player, PARENT_BONE);
                    data.boomboxEntity.transform.localPosition = BOOMBOX_LOCAL_POS;
                    data.boomboxEntity.transform.localRotation = Quaternion.identity;
                    data.boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    data.boomboxEntity.SendNetworkUpdateImmediate();

                    if (data.isPlaying)
                        StartAudio(data);

                    player.ChatMessage("<color=#FFD700>[Moo] Re-attached!</color>");
                });
            }
        }

        #endregion

        #region Audio

        private void StartAudio(MooData data)
        {
            if (data.boomboxEntity == null || data.boomboxEntity.IsDestroyed) return;

            var deployable = data.boomboxEntity as DeployableBoomBox;
            if (deployable == null) return;

            try
            {
                var controller = deployable.BoxController;
                if (controller == null) return;

                data.boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

                if (_currentRadioIpField != null)
                    _currentRadioIpField.SetValue(controller, AUDIO_URL);

                controller.baseEntity?.SendNetworkUpdateImmediate();
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

                var volumeField = typeof(BoomBox).GetField("BaseVolumeLevel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (volumeField != null)
                    volumeField.SetValue(controller, data.volume);

                var volumeProp = typeof(BoomBox).GetProperty("CurrentVolume",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (volumeProp != null && volumeProp.CanWrite)
                    volumeProp.SetValue(controller, data.volume);

                controller.baseEntity?.SendNetworkUpdateImmediate();
            }
            catch (Exception ex)
            {
                PrintWarning($"[Moo] Failed to set volume: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private bool HasMoo(BasePlayer player, out MooData data)
        {
            data = null;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                player.ChatMessage("<color=#ff4444>[Moo] No permission!</color>");
                return false;
            }

            if (!activeMoos.TryGetValue(player.userID, out data) ||
                data.boomboxEntity == null || data.boomboxEntity.IsDestroyed)
            {
                player.ChatMessage("<color=#ff4444>[Moo] No active boombox. Use /moo first.</color>");
                return false;
            }

            return true;
        }

        #endregion
    }
}
