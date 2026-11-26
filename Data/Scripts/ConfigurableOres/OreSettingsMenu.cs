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
using System.Globalization;
using System.Linq;
using System.Text;
using RichHudFramework.Client;
using RichHudFramework.UI;
using RichHudFramework.UI.Client;
using Sandbox.ModAPI;
using VRage.Game;
using static ConfigurableOres.Helpers;
using static ConfigurableOres.MenuStrings;
using static ConfigurableOres.ModInfo;
using static ConfigurableOres.Strings;

// ReSharper disable InconsistentNaming
namespace ConfigurableOres
{
    /// <summary>
    /// The Configurable Ores page in the Rich HUD settings menu (client only).
    ///
    /// The menu never changes settings itself. Every edit becomes an /ore command that is sent
    /// to the server through OreNetwork, so the menu and chat share one code path, one set of
    /// validation rules and one admin check. Controls read their values from the latest
    /// settings snapshot via CustomValueGetter, so they snap back if the server rejects a change
    /// or the player isn't allowed to edit.
    /// </summary>
    public static class OreSettingsMenu
    {
        /// <summary>Ticks to wait after the last slider movement before sending (60 ticks = 1 s).</summary>
        private const int SEND_DELAY_TICKS = 30;

        /// <summary>Ticks to keep showing sent values while waiting for the server's snapshot.</summary>
        private const int IN_FLIGHT_TIMEOUT_TICKS = 180;

        private static bool _initRequested;
        private static bool _built;
        private static bool _needSnapshot;
        private static int _tick;

        #region Pending edits

        private class Edit
        {
            public string Key;
            public object Value;
            public string Command;
        }

        // Edits waiting for the player to stop dragging, in the order they were made.
        private static readonly List<Edit> Pending = new List<Edit>();

        // Edits sent to the server whose result hasn't come back yet.
        private static readonly Dictionary<string, object> InFlight = new Dictionary<string, object>();
        private static int _inFlightCommands;
        private static int _inFlightDeadline;
        private static int _lastEditTick;

        #endregion

        #region Selection state

        private static string _selectedPlanet;
        private static string _selectedOre;
        private static bool _refreshing;

        #endregion

        #region Controls

        private static readonly List<TerminalSlider> Sliders = new List<TerminalSlider>();
        private static readonly Dictionary<TerminalSlider, int> SliderDecimals = new Dictionary<TerminalSlider, int>();

        private static TerminalLabel _statusAccess;
        private static TerminalLabel _statusRestart;
        private static TerminalLabel _statusResult;
        private static TerminalLabel _detectorRanges;
        private static TerminalLabel _chatCommandsState;
        private static TerminalButton _disableChatButton;
        private static bool _disableChatArmed;

        private static TerminalDropdown<string> _planetDropdown;
        private static TerminalDropdown<string> _orePagePlanetDropdown;
        private static TerminalLabel _planetInfo;
        private static TerminalList<string> _assignedOreList;
        private static TerminalButton _removeOreButton;
        private static TerminalDropdown<string> _addOreDropdown;
        private static TerminalButton _addOreButton;

        private static TerminalDropdown<string> _oreDropdown;
        private static TerminalSlider _raritySlider;

        // Controls only admins should see (buttons have no value to snap back).
        private static readonly List<TerminalControlBase> AdminOnly = new List<TerminalControlBase>();

        #endregion

        private static ConfigurableOres Mod => ConfigurableOres.Instance;
        private static Configuration Config => Mod?.Config;

        #region Lifecycle

        public static void Init()
        {
            if (_initRequested) return;
            _initRequested = true;

            RichHudClient.Init(MOD_NAME, OnRichHudReady, OnRichHudReset);
        }

        public static void Close()
        {
            OnRichHudReset();
            _initRequested = false;
            _selectedPlanet = null;
            _selectedOre = null;
        }

        private static void OnRichHudReady()
        {
            try
            {
                Build();
                _built = true;

                // Rich HUD can be ready before the world has loaded, so the first
                // request for settings waits for the first Update() tick.
                _needSnapshot = true;
            }
            catch (Exception e)
            {
                Log(LOG_PREFIX_MENU, "Could not build settings menu: " + e);
            }
        }

        private static void OnRichHudReset()
        {
            // Rich HUD dropped this client (session unloading or an error). Its controls are gone.
            _built = false;
            _needSnapshot = false;
            OreNetwork.ConfigChanged -= OnConfigChanged;
            OreNetwork.MenuCommandFinished -= OnCommandFinished;
            Pending.Clear();
            InFlight.Clear();
            _inFlightCommands = 0;
            Sliders.Clear();
            SliderDecimals.Clear();
            AdminOnly.Clear();
        }

        /// <summary>Called every tick by the session component.</summary>
        public static void Update()
        {
            if (!_built) return;
            _tick++;

            if (_needSnapshot)
            {
                _needSnapshot = false;
                OreNetwork.ConfigChanged += OnConfigChanged;
                OreNetwork.MenuCommandFinished += OnCommandFinished;
                OreNetwork.RequestConfig();
            }

            // Send edits once the player has stopped moving sliders for a moment.
            if (Pending.Count > 0 && _tick - _lastEditTick >= SEND_DELAY_TICKS)
                FlushPending();

            if (_inFlightCommands == 0 && InFlight.Count > 0 && _tick >= _inFlightDeadline)
                InFlight.Clear();

            // Labels and slider text only matter while the menu is open.
            if (_tick % 10 == 0 && RichHudTerminal.Open)
                RefreshText();
        }

        #endregion

        #region Building the menu

        private static void Build()
        {
            var root = RichHudTerminal.Root;
            root.Name = MOD_NAME;

            root.Add(BuildStatusPage());
            root.Add(BuildWorldPage(isDepth: true));
            root.Add(BuildWorldPage(isDepth: false));
            root.Add(BuildPlanetsPage());
            root.Add(BuildPlanetOresPage());
            root.Add(BuildModPage());
            root.Add(BuildHelpPage());

            // Shown once the first snapshot says whether this player may see it.
            root.Enabled = false;
        }

        private static ControlPage BuildStatusPage()
        {
            _statusAccess = new TerminalLabel { Name = "Waiting for the server..." };
            _statusRestart = new TerminalLabel { Name = "" };
            _statusResult = new TerminalLabel { Name = "" };

            var rebuild = AdminButton("Regenerate ore placement",
                "Re-rolls deposit depths and sizes on every planet using the current settings. Applies after a world restart.",
                () => Send("World Rebuild"));

            var resetDepth = AdminButton("Reset world depth settings", "Puts every world depth setting back to its default.",
                () => Send("World Depth Reset"));

            var resetSize = AdminButton("Reset world size settings", "Puts every world size setting back to its default.",
                () => Send("World Size Reset"));

            return Page("Status",
                Category("Configurable Ores", MODINFO_VERSION_TEXT,
                    Tile(_statusAccess, _statusRestart, _statusResult)),
                Category("Actions", "Changes apply after the world is restarted.",
                    Tile(rebuild, resetDepth, resetSize)));
        }

        private static ControlPage BuildWorldPage(bool isDepth)
        {
            Func<string> scope = () => "World";

            if (isDepth)
            {
                _detectorRanges = new TerminalLabel { Name = "" };
                var cap = Slider("Depth cap with detector range (0 = off)", Help("depth_detector_cap"),
                    0, 5000, 0, () => Config?.DetectorDepthCap, () => "World Depth DetectorCap");

                return Page("World depth",
                    Category("World depth", "How deep ore deposits are placed on every planet, unless a planet or ore overrides it.",
                        Tile(_detectorRanges, cap)),
                    DepthCategory("Settings", () => Config?.Depth, scope));
            }

            return Page("World size",
                SizeCategory("World size", () => Config?.Size, scope));
        }

        private static ControlPage BuildPlanetsPage()
        {
            _planetDropdown = new TerminalDropdown<string> { Name = "Planet" };
            _planetDropdown.ControlChanged += (s, a) => OnPlanetPicked(_planetDropdown);
            _planetInfo = new TerminalLabel { Name = "" };

            _assignedOreList = new TerminalList<string> { Name = "Ores on this planet" };

            _removeOreButton = AdminButton("Remove selected ore", "Takes the ore selected above off this planet.", () =>
            {
                var ore = _assignedOreList.Value?.AssocObject;
                if (_selectedPlanet != null && ore != null) Send($"{_selectedPlanet} Del {ore}");
            });

            _addOreDropdown = new TerminalDropdown<string> { Name = "Ore to add" };
            _addOreButton = AdminButton("Add ore", "Adds the chosen ore to this planet with the planet's average rarity.", () =>
            {
                var ore = _addOreDropdown.Value?.AssocObject;
                if (_selectedPlanet != null && ore != null) Send($"{_selectedPlanet} Add {ore}");
            });
            AdminOnly.Add(_addOreDropdown);

            Func<string> scope = () => _selectedPlanet;

            return Page("Planets",
                Category("Planet", "Pick a planet to edit. Planets with no ore slots aren't listed.",
                    Tile(_planetDropdown, _planetInfo)),
                Category("Ores", "Ores that can spawn on this planet.",
                    Tile(_assignedOreList, _removeOreButton),
                    Tile(_addOreDropdown, _addOreButton)),
                DepthCategory("Planet depth", () => SelectedPlanetConfig?.Depth, scope),
                SizeCategory("Planet size", () => SelectedPlanetConfig?.Size, scope));
        }

        private static ControlPage BuildPlanetOresPage()
        {
            _orePagePlanetDropdown = new TerminalDropdown<string> { Name = "Planet" };
            _orePagePlanetDropdown.ControlChanged += (s, a) => OnPlanetPicked(_orePagePlanetDropdown);

            _oreDropdown = new TerminalDropdown<string> { Name = "Ore" };
            _oreDropdown.ControlChanged += (s, a) =>
            {
                if (_refreshing) return;
                _selectedOre = _oreDropdown.Value?.AssocObject;
                RefreshText();
            };

            Func<string> scope = () =>
                _selectedPlanet != null && _selectedOre != null ? $"{_selectedPlanet} {_selectedOre}" : null;

            _raritySlider = Slider("Rarity",
                "How many of the planet's ore slots this ore gets. Higher means more common.",
                1, 10, 0, () => SelectedOre?.Rarity,
                () => scope() == null ? null : scope() + " Rarity");

            return Page("Planet ores",
                Category("Ore", "Pick a planet and one of its ores.",
                    Tile(_orePagePlanetDropdown, _oreDropdown),
                    Tile(_raritySlider)),
                DepthCategory("Ore depth", () => SelectedOre?.Depth, scope),
                SizeCategory("Ore size", () => SelectedOre?.Size, scope));
        }

        private static ControlPage BuildModPage()
        {
            var logging = OnOff("Logging", Help("mod_logging"), () => Config?.Logging,
                () => "World Mod Logging");

            var hideMenu = OnOff("Hide this menu from non-admins", Help("mod_hide_menu"),
                () => Config?.HideMenuFromNonAdmins, () => "World Mod HideMenu");

            _chatCommandsState = new TerminalLabel { Name = "" };
            _disableChatButton = AdminButton("Disable chat commands",
                "Turns off /ore chat commands for everyone. This menu keeps working. Press twice to confirm.",
                () =>
                {
                    if (!_disableChatArmed)
                    {
                        _disableChatArmed = true;
                        _disableChatButton.Name = "Press again to disable chat commands";
                        return;
                    }

                    _disableChatArmed = false;
                    _disableChatButton.Name = "Disable chat commands";
                    Send("World Mod Confirm DisableChatCommands");
                });

            return Page("Mod",
                Category("Mod settings", "",
                    Tile(logging, hideMenu),
                    Tile(_chatCommandsState, _disableChatButton)));
        }

        private static TextPage BuildHelpPage()
        {
            var text = new StringBuilder();
            text.AppendLine("Every change here is checked and saved by the server, exactly like the /ore chat commands.");
            text.AppendLine("Ore placement only changes when the world loads, so restart the world after making changes.");
            text.AppendLine();
            text.AppendLine("Settings are layered: World values apply everywhere, a planet's values override the world, and an ore's values override its planet.");
            text.AppendLine();

            foreach (var topic in new[]
                     {
                         "depth_min", "depth_max", "depth_use_detector", "depth_use_progressive", "depth_curve",
                         "depth_fuzz", "depth_detector_size", "depth_detector_factor",
                         "size_min", "size_max", "size_factor", "size_fuzz", "ore_rarity"
                     })
            {
                var help = Help(topic);
                if (!string.IsNullOrEmpty(help)) text.AppendLine(help).AppendLine();
            }

            text.AppendLine("Guide: " + MODINFO_MOD_URL);

            var page = new TextPage
            {
                Name = "Help",
                HeaderText = MOD_NAME,
                SubHeaderText = "How the settings work",
                Text = text.ToString()
            };
            page.Enabled = true;
            return page;
        }

        /// <summary>The depth controls, shared by the world, planet and ore pages.</summary>
        private static ControlCategory DepthCategory(string header, Func<MyDepthSettings> settings, Func<string> scope)
        {
            Func<string, Func<string>> cmd = item => () => scope() == null ? null : $"{scope()} Depth {item}";

            var min = Slider("Minimum depth", Hint("depth_min"), 0, 500, 0, () => settings()?.Min, cmd("Min"));
            var max = Slider("Maximum depth", Hint("depth_max"), 1, 2000, 0, () => settings()?.Max, cmd("Max"));
            var useDetector = OnOff("Max depth from ore detector range", Help("depth_use_detector"),
                () => settings()?.UseDetector, cmd("UseDetector"));
            var progressive = OnOff("Progressive depth", Help("depth_use_progressive"),
                () => settings()?.UseProgressive, cmd("UseProgressive"));
            var curve = Slider("Depth curve", Help("depth_curve"), 0.1f, 3f, 2, () => settings()?.Curve, cmd("Curve"));
            var fuzz = Slider("Depth fuzz", Help("depth_fuzz"), 0.01f, 2f, 2, () => settings()?.Fuzz, cmd("Fuzz"));
            var factor = Slider("Detector range factor", Help("depth_detector_factor"), 0.1f, 5f, 2,
                () => settings()?.DetectorFactor, cmd("DetectorFactor"));
            var detectorSize = DetectorSizeDropdown(settings, cmd("DetectorSize"));

            return Category(header, "",
                Tile(min, max),
                Tile(useDetector, progressive),
                Tile(curve, fuzz),
                Tile(detectorSize, factor));
        }

        /// <summary>The size controls, shared by the world, planet and ore pages.</summary>
        private static ControlCategory SizeCategory(string header, Func<MySizeSettings> settings, Func<string> scope)
        {
            Func<string, Func<string>> cmd = item => () => scope() == null ? null : $"{scope()} Size {item}";

            var min = Slider("Minimum size", Hint("size_min"), 1, 100, 0, () => settings()?.Min, cmd("Min"));
            var max = Slider("Maximum size", Hint("size_max"), 1, 200, 0, () => settings()?.Max, cmd("Max"));
            var factor = Slider("Size factor", Help("size_factor"), 0.01f, 1f, 2, () => settings()?.Factor, cmd("Factor"));
            var fuzz = Slider("Size fuzz", Help("size_fuzz"), 0.01f, 2f, 2, () => settings()?.Fuzz, cmd("Fuzz"));

            return Category(header, "How big ore deposits are.",
                Tile(min, max),
                Tile(factor, fuzz));
        }

        #endregion

        #region Control factories

        private static int _keyCounter;

        /// <summary>
        /// A slider bound to a setting. getValue returns null when there's nothing to show
        /// (no planet selected); command returns the /ore command without its value.
        /// </summary>
        private static TerminalSlider Slider(string name, string tooltip, float min, float max, int decimals,
            Func<float?> getValue, Func<string> command)
        {
            var key = "slider" + _keyCounter++;
            var slider = new TerminalSlider { Name = name, Min = min, Max = max };
            if (!string.IsNullOrEmpty(tooltip)) slider.ToolTip = tooltip;

            slider.CustomValueGetter = () =>
            {
                object shown;
                if (TryGetUnsent(key, out shown)) return (float)shown;
                return getValue() ?? slider.Min;
            };

            slider.ControlChanged += (s, a) =>
            {
                var value = (float)Math.Round(slider.Value, decimals);
                slider.ValueText = FormatNumber(value, decimals);

                if (_refreshing || !OreNetwork.CanEdit) return;

                var current = getValue();
                var cmd = command();
                if (current == null || cmd == null) return;

                object shown;
                var effective = TryGetUnsent(key, out shown) ? (float)shown : current.Value;
                if (Math.Abs(value - effective) < 0.5 * Math.Pow(10, -decimals)) return;

                Queue(key, value, $"{cmd} {FormatNumber(value, decimals)}");
            };

            Sliders.Add(slider);
            SliderDecimals[slider] = decimals;
            return slider;
        }

        private static TerminalOnOffButton OnOff(string name, string tooltip, Func<bool?> getValue, Func<string> command)
        {
            var key = "onoff" + _keyCounter++;
            var button = new TerminalOnOffButton { Name = name };
            if (!string.IsNullOrEmpty(tooltip)) button.ToolTip = tooltip;

            button.CustomValueGetter = () =>
            {
                object shown;
                if (TryGetUnsent(key, out shown)) return (bool)shown;
                return getValue() ?? false;
            };

            button.ControlChanged += (s, a) =>
            {
                if (_refreshing || !OreNetwork.CanEdit) return;

                var current = getValue();
                var cmd = command();
                if (current == null || cmd == null) return;

                var value = button.Value;
                object shown;
                var effective = TryGetUnsent(key, out shown) ? (bool)shown : current.Value;
                if (value == effective) return;

                Queue(key, value, $"{cmd} {(value ? "true" : "false")}");
            };

            return button;
        }

        private static TerminalDropdown<MyCubeSize> DetectorSizeDropdown(Func<MyDepthSettings> settings, Func<string> command)
        {
            var dropdown = new TerminalDropdown<MyCubeSize> { Name = "Detector used for max depth" };
            dropdown.ToolTip = Help("depth_detector_size");
            dropdown.List.Add("Large grid", MyCubeSize.Large);
            dropdown.List.Add("Small grid", MyCubeSize.Small);

            dropdown.CustomValueGetter = () =>
            {
                var size = settings()?.DetectorSize ?? MyCubeSize.Large;
                return dropdown.List.FirstOrDefault(e => e.AssocObject == size);
            };

            dropdown.ControlChanged += (s, a) =>
            {
                if (_refreshing || !OreNetwork.CanEdit) return;

                var current = settings();
                var cmd = command();
                var picked = dropdown.Value;
                if (current == null || cmd == null || picked == null) return;
                if (picked.AssocObject == current.DetectorSize) return;

                Send($"{cmd} {picked.AssocObject}");
            };

            return dropdown;
        }

        private static TerminalButton AdminButton(string name, string tooltip, Action onPress)
        {
            var button = new TerminalButton { Name = name };
            if (!string.IsNullOrEmpty(tooltip)) button.ToolTip = tooltip;
            button.ControlChanged += (s, a) =>
            {
                if (OreNetwork.CanEdit) onPress();
            };

            AdminOnly.Add(button);
            return button;
        }

        private static ControlPage Page(string name, params ControlCategory[] categories)
        {
            var page = new ControlPage { Name = name };
            foreach (var category in categories) page.Add(category);
            page.Enabled = true;
            return page;
        }

        private static ControlCategory Category(string header, string subheader, params ControlTile[] tiles)
        {
            var category = new ControlCategory { HeaderText = header, SubheaderText = subheader };
            foreach (var tile in tiles) category.Add(tile);
            return category;
        }

        private static ControlTile Tile(params TerminalControlBase[] controls)
        {
            var tile = new ControlTile();
            foreach (var control in controls) tile.Add(control);
            return tile;
        }

        #endregion

        #region Sending edits

        private static void Queue(string key, object value, string command)
        {
            Pending.RemoveAll(e => e.Key == key);
            Pending.Add(new Edit { Key = key, Value = value, Command = command });
            _lastEditTick = _tick;
        }

        private static void FlushPending()
        {
            foreach (var edit in Pending)
            {
                InFlight[edit.Key] = edit.Value;
                Send(edit.Command);
            }

            Pending.Clear();
        }

        private static void Send(string command)
        {
            if (!OreNetwork.CanEdit) return;

            _inFlightCommands++;
            _inFlightDeadline = _tick + IN_FLIGHT_TIMEOUT_TICKS;
            OreNetwork.RunCommand(command, fromChat: false);
        }

        private static bool TryGetUnsent(string key, out object value)
        {
            foreach (var edit in Pending)
            {
                if (edit.Key != key) continue;
                value = edit.Value;
                return true;
            }

            return InFlight.TryGetValue(key, out value);
        }

        #endregion

        #region Reacting to the server

        private static void OnConfigChanged()
        {
            if (_inFlightCommands == 0) InFlight.Clear();

            var config = Config;
            if (config == null) return;

            // Hidden from non-admins only when an admin has chosen that.
            RichHudTerminal.Root.Enabled = OreNetwork.CanEdit || !config.HideMenuFromNonAdmins;

            foreach (var control in AdminOnly)
                control.Enabled = OreNetwork.CanEdit;

            RefreshPlanetLists();
            RefreshText();
        }

        private static void OnCommandFinished(bool success, string output)
        {
            _inFlightCommands = Math.Max(0, _inFlightCommands - 1);

            // A rejected change won't be followed by a snapshot, so drop the shown value now.
            if (!success && _inFlightCommands == 0) InFlight.Clear();

            var firstLine = FirstLine(output);
            _lastResult = (success ? "Last change: " : "Not changed: ") + firstLine;

            if (!success)
            {
                MyAPIGateway.Utilities.ShowNotification($"{MOD_NAME}: {firstLine}", 5000, MyFontEnum.Red);
                if (!string.IsNullOrEmpty(output)) MyAPIGateway.Utilities.ShowMessage(MOD_CHAT_AUTHOR, output);
            }

            RefreshText();
        }

        private static string _lastResult = "";

        #endregion

        #region Refreshing controls

        private static MyPlanetConfiguration SelectedPlanetConfig =>
            _selectedPlanet != null && Config != null && Config.MyPlanetConfigurations.Contains(_selectedPlanet)
                ? Config.MyPlanetConfigurations.Get(_selectedPlanet)
                : null;

        private static MyPlanetOreAssignment SelectedOre =>
            _selectedOre == null
                ? null
                : SelectedPlanetConfig?.GetNonStaticVoxelOreAssignments()
                    .FirstOrDefault(o => o.VoxelMinedOre == _selectedOre);

        private static void OnPlanetPicked(TerminalDropdown<string> source)
        {
            if (_refreshing) return;

            _selectedPlanet = source.Value?.AssocObject;
            _selectedOre = null;
            RefreshPlanetLists();
            RefreshText();
        }

        /// <summary>Rebuilds the planet and ore lists, keeping the current selection where possible.</summary>
        private static void RefreshPlanetLists()
        {
            var config = Config;
            if (config == null) return;

            _refreshing = true;
            try
            {
                var planets = config.MyPlanetConfigurations.GetNames()
                    .Where(n => config.MyPlanetConfigurations.Get(n).OreSlotCount > 0)
                    .OrderBy(n => n)
                    .ToList();

                if (_selectedPlanet == null || !planets.Contains(_selectedPlanet))
                    _selectedPlanet = planets.FirstOrDefault();

                SetEntries(_planetDropdown.List, planets, _selectedPlanet);
                SetEntries(_orePagePlanetDropdown.List, planets, _selectedPlanet);

                var planet = SelectedPlanetConfig;
                var assigned = planet?.GetAssignedOres().Distinct().OrderBy(o => o).ToList() ?? new List<string>();

                if (_selectedOre == null || !assigned.Contains(_selectedOre))
                    _selectedOre = assigned.FirstOrDefault();

                SetEntries(_assignedOreList.List, assigned, _assignedOreList.Value?.AssocObject);
                SetEntries(_oreDropdown.List, assigned, _selectedOre);

                var addable = Mod?.VoxelOres.GetUsableOres().Distinct().Where(o => !assigned.Contains(o)).OrderBy(o => o).ToList()
                              ?? new List<string>();
                SetEntries(_addOreDropdown.List, addable, _addOreDropdown.Value?.AssocObject);

                // Rarity can't exceed the planet's usable ore slots.
                if (planet != null)
                    _raritySlider.Max = Math.Max(1, planet.OreSlotCount - planet.StaticOres.Count);
            }
            finally
            {
                _refreshing = false;
            }
        }

        private static void SetEntries(ListBoxData<string> list, List<string> items, string select)
        {
            var same = list.Count == items.Count;
            for (var i = 0; same && i < items.Count; i++)
                same = list[i].AssocObject == items[i];

            if (!same)
            {
                list.Clear();
                foreach (var item in items) list.Add(item, item);
            }

            if (select != null && items.Contains(select)) list.SetSelection(select);
            else if (items.Count > 0 && list.Selection == null) list.SetSelection(0);
        }

        private static void RefreshText()
        {
            if (!_built) return;

            SetName(_statusAccess, !OreNetwork.HasSnapshot
                ? "Waiting for the server..."
                : OreNetwork.CanEdit
                    ? "You can change these settings."
                    : "Read only: only admins can change these settings.");

            SetName(_statusRestart, OreNetwork.RestartNeeded
                ? "Settings changed. Restart the world to apply them."
                : "No changes waiting for a restart.");

            SetName(_statusResult, _lastResult);

            var ranges = Mod?.OreDetectorRanges;
            float large, small;
            if (ranges != null && ranges.TryGetValue(MyCubeSize.Large, out large) &&
                ranges.TryGetValue(MyCubeSize.Small, out small))
            {
                var cap = Config?.DetectorDepthCap ?? 0;
                SetName(_detectorRanges, cap > 0
                    ? $"Longest ore detector range: large grid {large:0} m, small grid {small:0} m. Ore is capped at {cap} m."
                    : $"Longest ore detector range: large grid {large:0} m, small grid {small:0} m");
            }

            var config = Config;
            if (config != null)
            {
                SetName(_chatCommandsState, config.DisableChatCommands
                    ? "Chat commands are disabled. Edit ConfigurableOres.xml to turn them back on."
                    : $"Chat commands ({COMMAND_PREFIX}) are enabled.");
                _disableChatButton.Enabled = OreNetwork.CanEdit && !config.DisableChatCommands;
            }

            var planet = SelectedPlanetConfig;
            SetName(_planetInfo, planet == null
                ? ""
                : $"{planet.OreSlotCount} ore slots, {planet.GetAssignedOres().Distinct().Count()} ores assigned");

            foreach (var slider in Sliders)
                slider.ValueText = FormatNumber(slider.Value, SliderDecimals[slider]);
        }

        private static void SetName(TerminalControlBase control, string name)
        {
            if (control != null && control.Name != name) control.Name = name;
        }

        #endregion

        #region Formatting

        private static string FormatNumber(float value, int decimals)
        {
            // Invariant so the server parses it the same way whatever language the game is in.
            return Math.Round(value, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.FirstOrDefault() ?? "";
        }

        #endregion
    }
}
