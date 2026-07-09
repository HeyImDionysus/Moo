// ============================================================================
// Moo — Invisible Boombox Plugin for Rust (Oxide/uMod)
// Plays Doja Cat - Mooo! from an invisible boombox attached to the player.
// ============================================================================
// Setup:
//   1. Drop this file into oxide/plugins/
//   2. Add audio URL to server config:
//        BoomBox.ServerUrlList "Mooo,https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3"
//   3. Grant permission:  oxide.grant user <steamid> moo.use
//
// Commands:
//   /moo        — Toggle the invisible boombox on/off
//   /moo.remove — Remove the boombox
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
    [Description("Invisible portable boombox that plays Doja Cat - Mooo!")]
    class Moo : RustPlugin
    {
        private const string PERM_USE = "moo.use";
        private const string BOOMBOX_PREFAB = "assets/prefabs/voiceaudio/boombox/boombox.deployed.prefab";
        private const string AUDIO_URL = "https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3";

        private class MooData
        {
            public BaseEntity boomboxEntity;
            public ulong ownerID;
            public bool isPlaying;
            public Timer followTimer;

            public void Cleanup()
            {
                followTimer?.Destroy();
                followTimer = null;
            }
        }

        // Owner steamID → boombox data
        private readonly Dictionary<ulong, MooData> activeMoos = new Dictionary<ulong, MooData>();

        // Entity net ID → moo data (for CanNetworkTo)
        private readonly Dictionary<NetworkableId, MooData> entityLookup = new Dictionary<NetworkableId, MooData>();

        // Reflection for BoomBox URL
        private FieldInfo _currentRadioIpField;

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

            if (activeMoos.TryGetValue(player.userID, out var existing))
            {
                if (existing.boomboxEntity != null && !existing.boomboxEntity.IsDestroyed)
                {
                    // Toggle play/stop
                    if (existing.isPlaying)
                    {
                        StopAudio(existing);
                        player.ChatMessage("<color=#FFD700>🐄 Moo stopped.</color>");
                    }
                    else
                    {
                        StartAudio(existing);
                        player.ChatMessage("<color=#FFD700>🐄 Mooo! Playing...</color>");
                    }
                    return;
                }
                DestroyMoo(existing);
                activeMoos.Remove(player.userID);
            }

            SpawnMoo(player);
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
            var offset = new Vector3(0f, 1.0f, 0f);
            var pos = player.transform.position + offset;

            var boomboxEntity = GameManager.server.CreateEntity(BOOMBOX_PREFAB, pos);
            if (boomboxEntity == null)
            {
                player.ChatMessage("<color=#ff4444>Failed to spawn boombox.</color>");
                return;
            }

            boomboxEntity.OwnerID = player.userID;
            boomboxEntity.SetParent(player, "");
            boomboxEntity.transform.localPosition = offset;
            boomboxEntity.transform.localRotation = Quaternion.identity;
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
                isPlaying = false
            };

            // Register for CanNetworkTo
            if (boomboxEntity.net != null)
                entityLookup[boomboxEntity.net.ID] = data;

            activeMoos[player.userID] = data;

            // Power and play
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
                player.ChatMessage("<size=18><color=#FFD700>🐄 Mooo!</color></size>");
                player.ChatMessage("<color=#aaaaaa>/moo to toggle, /moo.remove to remove</color>");

                Puts($"[Moo] {player.displayName} attached a Moo boombox");
            });

            // Follow timer (re-parent if detached, keep power on)
            data.followTimer = timer.Every(0.1f, () =>
            {
                if (boomboxEntity == null || boomboxEntity.IsDestroyed)
                {
                    data.Cleanup();
                    activeMoos.Remove(player.userID);
                    return;
                }

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

                if (!boomboxEntity.HasFlag(BaseEntity.Flags.Reserved8))
                    boomboxEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

                var cb = boomboxEntity as BaseCombatEntity;
                if (cb != null && cb.health < cb.MaxHealth())
                    cb.SetHealth(cb.MaxHealth());
            });
        }

        private void DestroyMoo(MooData data)
        {
            data.Cleanup();

            if (data.boomboxEntity?.net != null)
                entityLookup.Remove(data.boomboxEntity.net.ID);

            if (data.boomboxEntity != null && !data.boomboxEntity.IsDestroyed)
                data.boomboxEntity.Kill();
        }

        #endregion

        #region Hooks

        private object CanNetworkTo(BaseNetworkable entity, BasePlayer player)
        {
            if (entity?.net == null || player == null) return null;
            if (!entityLookup.TryGetValue(entity.net.ID, out var data)) return null;

            // Everyone can hear it (boombox hidden inside player model)
            return null;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return true;
            return null;
        }

        private object OnEntityDecay(BaseCombatEntity entity)
        {
            if (entity?.net == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return true;
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net == null || player == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return false;
            return null;
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity?.net == null || player == null) return null;
            if (entityLookup.ContainsKey(entity.net.ID))
                return false;
            return null;
        }

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
                var offset = new Vector3(0f, 1.0f, 0f);

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

                    player.ChatMessage("<color=#FFD700>🐄 Moo re-attached!</color>");
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

        #endregion
    }
}
