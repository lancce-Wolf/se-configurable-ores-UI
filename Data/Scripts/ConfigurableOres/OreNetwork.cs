/*
    Configurable Ores - A Space Engineers mod for managing planetary ores.
    Copyright 2022 Travis Wichert

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Text;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using static ConfigurableOres.Helpers;
using static ConfigurableOres.ModInfo;
using static ConfigurableOres.MenuStrings;
using static ConfigurableOres.Strings;

// ReSharper disable InconsistentNaming
namespace ConfigurableOres
{
    public enum OreMessageType : byte
    {
        /// <summary>Client asks the server for the current settings.</summary>
        RequestConfig = 0,

        /// <summary>Server sends the current settings to one client.</summary>
        ConfigSnapshot = 1,

        /// <summary>Client asks the server to run an /ore command (typed in chat or sent by the settings menu).</summary>
        RunCommand = 2,

        /// <summary>Server sends the output of a command back to the player who ran it.</summary>
        CommandResult = 3,
    }

    [ProtoContract]
    public class OreMessage
    {
        [ProtoMember(1)] public OreMessageType Type;

        /// <summary>Command text, settings XML, or command output, depending on Type.</summary>
        [ProtoMember(2)] public string Text;

        /// <summary>Snapshot only: whether the receiving player may change settings.</summary>
        [ProtoMember(3)] public bool CanEdit;

        /// <summary>Result only: whether the command succeeded.</summary>
        [ProtoMember(4)] public bool Success;

        /// <summary>Run and result: the command was typed in chat rather than sent by the menu.</summary>
        [ProtoMember(5)] public bool FromChat;

        /// <summary>Snapshot only: settings changed since the world loaded, so a restart is needed.</summary>
        [ProtoMember(6)] public bool RestartNeeded;
    }

    /// <summary>
    /// Client/server plumbing for changing settings at runtime.
    ///
    /// The server is the only machine that changes or saves the configuration. Clients send it
    /// /ore command text (from chat or from the settings menu); the server checks the sender is
    /// an admin, runs the command through the existing chat menu (so all validation is shared),
    /// sends the command's output back to the sender, and sends every player a fresh snapshot of
    /// the settings when something changed.
    ///
    /// In single player and on a listen server's own machine, the same path runs locally.
    /// </summary>
    public static class OreNetwork
    {
        /// <summary>Lowest rank that can change settings.</summary>
        public const MyPromoteLevel MIN_EDIT_LEVEL = MyPromoteLevel.Admin;

        #region Client-side view state

        /// <summary>The local player may change settings.</summary>
        public static bool CanEdit { get; private set; }

        /// <summary>Settings changed since the world loaded; they apply after a restart.</summary>
        public static bool RestartNeeded { get; private set; }

        /// <summary>At least one snapshot has arrived, so Config reflects the server.</summary>
        public static bool HasSnapshot { get; private set; }

        /// <summary>Raised on the client after a new snapshot of the settings arrives.</summary>
        public static event Action ConfigChanged;

        /// <summary>Raised on the client when a command sent from the menu finishes: (success, output).</summary>
        public static event Action<bool, string> MenuCommandFinished;

        #endregion

        private static bool _registered;

        /// <summary>Server only: the settings as they were applied when the world loaded.</summary>
        private static string _loadedConfigXml;

        private static bool _serverRestartNeeded;

        private static readonly List<IMyPlayer> PlayersBuffer = new List<IMyPlayer>();
        private static readonly List<IMyPlayer> LookupBuffer = new List<IMyPlayer>();

        private static bool IsServer =>
            !MyAPIGateway.Multiplayer.MultiplayerActive || MyAPIGateway.Multiplayer.IsServer;

        private static bool IsDedicated => MyAPIGateway.Utilities.IsDedicated;

        private static ConfigurableOres Mod => ConfigurableOres.Instance;

        #region Lifecycle

        public static void Register()
        {
            if (_registered) return;

            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(CHANNEL_ID, OnMessage);
            _registered = true;

            if (IsServer && Mod?.Config != null)
                _loadedConfigXml = Configuration.SerializeConfig(Mod.Config);
        }

        public static void Unregister()
        {
            if (_registered)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(CHANNEL_ID, OnMessage);

            _registered = false;
            _loadedConfigXml = null;
            _serverRestartNeeded = false;
            CanEdit = false;
            RestartNeeded = false;
            HasSnapshot = false;
            ConfigChanged = null;
            MenuCommandFinished = null;
            ChatCapture = null;
        }

        #endregion

        #region Client API

        /// <summary>Ask for the current settings. The answer raises ConfigChanged.</summary>
        public static void RequestConfig()
        {
            if (IsServer)
            {
                if (!IsDedicated) ApplySnapshot(BuildSnapshot(LocalSteamId));
                return;
            }

            SendToServer(new OreMessage { Type = OreMessageType.RequestConfig });
        }

        /// <summary>
        /// Run an /ore command on the server. The leading "/ore" is optional.
        /// Output goes to chat when fromChat is true, otherwise to MenuCommandFinished.
        /// </summary>
        public static void RunCommand(string commandText, bool fromChat)
        {
            if (string.IsNullOrWhiteSpace(commandText)) return;

            if (!IsMyMatch(COMMAND_PREFIX, commandText))
                commandText = COMMAND_PREFIX + " " + commandText;

            if (IsServer)
            {
                bool changed;
                var result = Execute(commandText, LocalSteamId, fromChat, out changed);
                DeliverResult(result);
                if (changed) BroadcastSnapshots();
                return;
            }

            SendToServer(new OreMessage
            {
                Type = OreMessageType.RunCommand,
                Text = commandText,
                FromChat = fromChat
            });
        }

        #endregion

        #region Message handling

        private static void OnMessage(ushort handlerId, byte[] data, ulong sender, bool fromServer)
        {
            OreMessage msg;
            try
            {
                msg = MyAPIGateway.Utilities.SerializeFromBinary<OreMessage>(data);
            }
            catch (Exception e)
            {
                Log(LOG_PREFIX_SESSION, $"Bad network message from {sender}: {e.Message}");
                return;
            }

            if (msg == null) return;

            switch (msg.Type)
            {
                case OreMessageType.RequestConfig:
                    if (!IsServer) return;
                    SendTo(sender, BuildSnapshot(sender));
                    break;

                case OreMessageType.RunCommand:
                {
                    if (!IsServer) return;
                    bool changed;
                    var result = Execute(msg.Text ?? "", sender, msg.FromChat, out changed);
                    SendTo(sender, result);
                    if (changed) BroadcastSnapshots();
                    break;
                }

                case OreMessageType.ConfigSnapshot:
                    if (!fromServer || IsServer) return;
                    ApplySnapshot(msg);
                    break;

                case OreMessageType.CommandResult:
                    if (!fromServer || IsServer) return;
                    DeliverResult(msg);
                    break;
            }
        }

        #endregion

        #region Server

        public static bool CanEditFor(ulong steamId)
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive) return true;

            var player = FindPlayer(steamId);
            return player != null && player.PromoteLevel >= MIN_EDIT_LEVEL;
        }

        private static OreMessage Execute(string commandText, ulong sender, bool fromChat, out bool changed)
        {
            changed = false;

            var result = new OreMessage { Type = OreMessageType.CommandResult, FromChat = fromChat };
            var config = Mod?.Config;

            if (config == null)
            {
                result.Text = "Configurable Ores is not loaded yet.";
                return result;
            }

            if (fromChat && config.DisableChatCommands)
            {
                result.Text = Warning("mod_chat_commands_are_disabled");
                return result;
            }

            if (!CanEditFor(sender))
            {
                result.Text = "Only admins can change Configurable Ores settings.";
                return result;
            }

            var before = Configuration.SerializeConfig(config);
            var output = new StringBuilder();
            var ok = false;

            ChatCapture = output;
            try
            {
                ok = Mod.RunMenuCommand(commandText);
                if (!ok) WriteErrorToChat(Error("default"), commandText);
            }
            catch (Exception e)
            {
                ok = false;
                output.AppendLine("Error: " + e.Message);
                Log(LOG_PREFIX_SESSION, e.ToString());
            }
            finally
            {
                ChatCapture = null;
            }

            var after = Configuration.SerializeConfig(Mod.Config);
            changed = after != before;
            if (changed) _serverRestartNeeded = after != _loadedConfigXml;

            result.Success = ok;
            result.Text = output.ToString().TrimEnd();
            return result;
        }

        private static OreMessage BuildSnapshot(ulong steamId)
        {
            return new OreMessage
            {
                Type = OreMessageType.ConfigSnapshot,
                Text = SnapshotXml(Mod.Config),
                CanEdit = CanEditFor(steamId),
                RestartNeeded = _serverRestartNeeded
            };
        }

        /// <summary>
        /// The settings without the generated ore mappings, which are the bulk of the file and
        /// which the menu doesn't need. Clients get the real mappings from the world when they join.
        /// </summary>
        private static string SnapshotXml(Configuration config)
        {
            var copy = Configuration.DeserializeConfig(Configuration.SerializeConfig(config));
            foreach (var member in copy.MyPlanetConfigurations.MemberPlanets)
                member.PlanetConfig?.GeneratedPlanetOreMappings?.Clear();

            return Configuration.SerializeConfig(copy);
        }

        private static void BroadcastSnapshots()
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive)
            {
                ApplySnapshot(BuildSnapshot(LocalSteamId));
                return;
            }

            PlayersBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(PlayersBuffer);

            foreach (var player in PlayersBuffer)
            {
                if (player == null || player.IsBot) continue;

                var snapshot = BuildSnapshot(player.SteamUserId);

                if (!IsDedicated && player.SteamUserId == LocalSteamId)
                    ApplySnapshot(snapshot);
                else
                    SendTo(player.SteamUserId, snapshot);
            }

            PlayersBuffer.Clear();
        }

        #endregion

        #region Client

        private static void ApplySnapshot(OreMessage msg)
        {
            // Remote clients keep a copy so the menu can show the server's settings.
            // This never re-applies ore mappings: those only change when the world loads.
            if (!IsServer && Mod != null && !string.IsNullOrEmpty(msg.Text))
            {
                try
                {
                    Mod.Config = Configuration.DeserializeConfig(msg.Text);
                }
                catch (Exception e)
                {
                    Log(LOG_PREFIX_SESSION, "Could not read settings from server: " + e.Message);
                    return;
                }
            }

            CanEdit = msg.CanEdit;
            RestartNeeded = msg.RestartNeeded;
            HasSnapshot = true;

            ConfigChanged?.Invoke();
        }

        private static void DeliverResult(OreMessage msg)
        {
            if (msg.FromChat)
            {
                if (!string.IsNullOrEmpty(msg.Text))
                    MyAPIGateway.Utilities.ShowMessage(MOD_CHAT_AUTHOR, msg.Text);
                return;
            }

            MenuCommandFinished?.Invoke(msg.Success, msg.Text ?? "");
        }

        #endregion

        #region Helpers

        private static ulong LocalSteamId => MyAPIGateway.Multiplayer.MyId;

        private static IMyPlayer FindPlayer(ulong steamId)
        {
            // Separate buffer: BroadcastSnapshots is iterating PlayersBuffer when this runs.
            LookupBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(LookupBuffer, p => p.SteamUserId == steamId);
            var player = LookupBuffer.Count > 0 ? LookupBuffer[0] : null;
            LookupBuffer.Clear();
            return player;
        }

        private static void SendToServer(OreMessage msg)
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(CHANNEL_ID,
                MyAPIGateway.Utilities.SerializeToBinary(msg));
        }

        private static void SendTo(ulong steamId, OreMessage msg)
        {
            MyAPIGateway.Multiplayer.SendMessageTo(CHANNEL_ID,
                MyAPIGateway.Utilities.SerializeToBinary(msg), steamId);
        }

        #endregion
    }
}
