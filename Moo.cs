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
    [Info("Moo", "Viktor", "1.1.0")]
    [Description("Invisible portable boombox that plays Doja Cat - Mooo!")]
    class Moo : RustPlugin
    {
        private const string PERM_USE = "moo.use";
        private const string BOOMBOX_PREFAB = "assets/prefabs/voiceaudio/boombox/boombox.deployed.prefab";
        private const string AUDIO_URL = "https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3";
        private static readonly Vector3 BOOMBOX_OFFSET = new Vector3(0f, 1.0f, 0f);
        private static readonly Vector3 INVISIBLE_SCALE = new Vector3(0.001f, 0.001f, 0.001f);

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

        // Net IDs of all active moo boomboxes (for protection hooks)
        private readonly HashSet<uint> _mooNetIds = new HashSet<uint>();

        // Net IDs of boomboxes in stealth mode (for CanNetworkTo fast-path)
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
            }
        }

        #endregion

        #region Chat Messaging

        /// <summary>
        /// Send a chat message to the player with "Moo" as the sender name
        /// instead of the default "?" that appears when using ChatMessage().
        /// </summary>
        private void SendMooMessage(BasePlayer player, string message)
        {
            player.SendConsoleCommand("chat.add", 2, 0, message, "Moo");
        }

        #endregion

        #region Chat Commands

        [ChatCommand("moo")]
        private void CmdMoo(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                SendMooMessage(player, "<color=#ff4444>You don't have permission to use Moo!</color>");
                return;
            }

            if (activeMoos.TryGetValue(player.userID, out var existing))
            {
                if (existing.boomboxEntity != null && !existing.boomboxEntity.IsDestroyed)
                {
                    if (existing.isPlaying)
                    {
                        StopAudio(existing);
                        SendMooMessage(player, "<color=#FFD700>🐄 Moo paused! Use /moo.play to resume or /moo.remove to remove.</color>");
                    }
                    else
                    {
                        StartAudio(existing);
                        SendMooMessage(player, "<color=#FFD700>🐄 Mooo! Playing...</color>");
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
            SendMooMessage(player, "<color=#FFD700>🐄 Mooo! Playing...</color>");
        }

        [ChatCommand("moo.stop")]
        private void CmdMooStop(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;
            StopAudio(data);
            SendMooMessage(player, "<color=#FFD700>🐄 Moo stopped.</color>");
        }

        [ChatCommand("moo.vol")]
        private void CmdMooVolume(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            if (args.Length == 0)
            {
                SendMooMessage(player, $"<color=#FFD700>🐄 Current volume: {data.volume * 10:F0}/10. Usage: /moo.vol 0-10</color>");
                return;
            }

            if (!float.TryParse(args[0], out float level) || level < 0 || level > 10)
            {
                SendMooMessage(player, "<color=#ff4444>Volume must be a number from 0 to 10.</color>");
                return;
            }

            data.volume = level / 10f;
            ApplyVolume(data);
            SendMooMessage(player, $"<color=#FFD700>🐄 Volume set to {level:F0}/10</color>");
        }

        [ChatCommand("moo.stealth")]
        private void CmdMooStealth(BasePlayer player, string command, string[] args)
        {
            if (!HasMoo(player, out var data)) return;

            data.isStealth = !data.isStealth;

            // Update stealth tracking set for CanNetworkTo fast-path
            if (data.boomboxEntity?.net != null)
            {
                uint netId = data.boomboxEntity.net.ID.Value;
                if (data.isStealth)
                    _stealthNetIds.Add(netId);
                else
                    _stealthNetIds.Remove(netId);
            }

            if (data.boomboxEntity != null)
                data.boomboxEntity.SendNetworkUpdateImmediate();

            string state = data.isStealth ? "ON — only you can hear it" : "OFF — everyone nearby can hear it";
            SendMooMessage(player, $"<color=#FFD700>🐄 Stealth mode: {state}</color>");
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
                SendMooMessage(player, "<color=#FFD700>🐄 Moo removed!</color>");
            }
            else
            {
                SendMooMessage(player, "<color=#ff4444>You don't have an active Moo boombox.</color>");
            }
        }

        #endregion

        #region Spawn / Destroy

        private void SpawnMoo(BasePlayer player)
        {
            var pos = player.transform.position + BOOMBOX_OFFSET;

            var boomboxEntity = GameManager.server.CreateEntity(BOOMBOX_PREFAB, pos);
            if (boomboxEntity == null)
            {
                SendMooMessage(player, "<color=#ff4444>Failed to spawn boombox.</color>");
                return;
            }

            boomboxEntity.OwnerID = player.userID;
            boomboxEntity.enableSaving = false;
            boomboxEntity.SetParent(player, "");
            boomboxEntity.transform.localPosition = BOOMBOX_OFFSET;
            boomboxEntity.transform.localRotation = Quaternion.identity;
            boomboxEntity.transform.localScale = INVISIBLE_SCALE;
            boomboxEntity.Spawn();

            // Disable ground checks
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

            // Power and auto-play
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
                SendMooMessage(player, "<size=18><color=#FFD700>🐄 Mooo! — Boombox attached and playing!</color></size>");
                SendMooMessage(player, "<color=#aaaaaa>Commands: /moo (toggle), /moo.stop, /moo.play, /moo.vol 0-10, /moo.stealth, /moo.remove</color>");

                Puts($"[Moo] {player.displayName} attached a Moo boombox");
            });

            // Follow timer — safety net to re-parent if detached (runs every 2s instead of 0.1s)
            data.followTimer = timer.Every(2f, () =>
            {
                if (boomboxEntity == null || boomboxEntity.IsDestroyed)
                {
                    data.Cleanup();
                    activeMoos.Remove(player.userID);
                    return;
                }

                bool needsUpdate = false;

                // Only re-parent if actually detached
                var parent = boomboxEntity.GetParentEntity();
                if (parent != player)
                {
                    if (player != null && player.IsConnected && !player.IsDead())
                    {
                        boomboxEntity.SetParent(player, "");
                        boomboxEntity.transform.localPosition = BOOMBOX_OFFSET;
                        boomboxEntity.transform.localRotation = Quaternion.identity;
                        boomboxEntity.transform.localScale = INVISIBLE_SCALE;
                        needsUpdate = true;
                    }
                }

                // Ensure power stays on
                if (!boomboxEntity.HasFlag(BaseEntity.Flags.Reserved8))
                {
                    boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    needsUpdate = true;
                }

                // Keep health topped up
                var cb = boomboxEntity as BaseCombatEntity;
                if (cb != null && cb.health < cb.MaxHealth())
                    cb.SetHealth(cb.MaxHealth());

                // Only send a network update if something actually changed
                if (needsUpdate)
                    boomboxEntity.SendNetworkUpdateImmediate();
            });
        }

        private void DestroyMoo(MooData data)
        {
            data.Cleanup();

            if (data.boomboxEntity?.net != null)
            {
                _mooNetIds.Remove(data.boomboxEntity.net.ID.Value);
                _stealthNetIds.Remove(data.boomboxEntity.net.ID.Value);
            }

            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
                data.boomboxEntity.Kill();
        }

        #endregion

        #region Hooks — Visibility (Stealth only)

        private object CanNetworkTo(BaseNetworkable entity, BasePlayer player)
        {
            // Fast exit: if nothing is in stealth, skip all work immediately
            if (_stealthNetIds.Count == 0) return null;
            if (entity?.net == null) return null;

            // Only process entities we're actively tracking as stealth
            if (!_stealthNetIds.Contains(entity.net.ID.Value)) return null;

            var baseEnt = entity as BaseEntity;
            if (baseEnt == null) return null;

            // Stealth: owner always receives (hears audio); everyone else is blocked
            if (player.userID == baseEnt.OwnerID) return null;
            return false;
        }

        #endregion

        #region Hooks — Protection

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net == null) return null;
            if (_mooNetIds.Contains(entity.net.ID.Value))
                return true;
            return null;
        }

        private object OnEntityDecay(BaseCombatEntity entity)
        {
            if (entity?.net == null) return null;
            if (_mooNetIds.Contains(entity.net.ID.Value))
                return true;
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net == null || player == null) return null;
            if (_mooNetIds.Contains(entity.net.ID.Value))
                return false;
            return null;
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net == null || player == null) return null;
            if (_mooNetIds.Contains(entity.net.ID.Value))
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

                    data.boomboxEntity.SetParent(player, "");
                    data.boomboxEntity.transform.localPosition = BOOMBOX_OFFSET;
                    data.boomboxEntity.transform.localRotation = Quaternion.identity;
                    data.boomboxEntity.transform.localScale = INVISIBLE_SCALE;
                    data.boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    data.boomboxEntity.SendNetworkUpdateImmediate();

                    if (data.isPlaying)
                        StartAudio(data);

                    SendMooMessage(player, "<color=#FFD700>🐄 Moo re-attached!</color>");
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
                SendMooMessage(player, "<color=#ff4444>You don't have permission to use Moo!</color>");
                return false;
            }

            if (!activeMoos.TryGetValue(player.userID, out data) ||
                data.boomboxEntity == null || data.boomboxEntity.IsDestroyed)
            {
                SendMooMessage(player, "<color=#ff4444>You don't have an active Moo boombox. Use /moo to attach one.</color>");
                return false;
            }

            return true;
        }

        #endregion
    }
}
