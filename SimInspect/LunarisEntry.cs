using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Lunaris;

[assembly: AssemblyMetadata("LunarisPluginId", "siminspect")]

namespace SimInspect
{
    [LunarisPlugin("Sim Inspector", "0.3.2", "xJeris",
        "Inspect any sim player's gear and stats, regardless of zone or proximity.")]
    public class LunarisEntry : LunarisPlugin
    {
        public static LunarisConfig Settings { get; private set; }

        // Track whether we set GameData.DraggingUIElement so we only clear our own flag
        private bool _ownsDragFlag;

        // Expose window state as statics so the Harmony patch can compute mouse-over
        // on-demand (avoids frame-ordering issues between our Update and CameraController)
        internal static bool S_ShowSelector;
        internal static bool S_ShowInspect;
        internal static Rect S_SelectorRect;
        internal static Rect S_InspectRect;

        // UI state
        private bool _showSelector;
        private bool _showInspect;
        private string _searchFilter = "";
        private Vector2 _selectorScroll;
        private Vector2 _inspectScroll;
        private Rect _selectorRect = new Rect(20, 60, 320, 500);
        private Rect _inspectRect = new Rect(360, 60, 520, 520);

        // Resize state
        private bool _isResizingSelector;
        private bool _isResizingInspect;
        private const float ResizeHandleSize = 16f;
        private const float MinWindowWidth = 300f;
        private const float MinWindowHeight = 300f;

        // Inspected sim data
        private SimPlayerSaveData _inspectedData;
        private string _inspectedName;
        private ComputedStats _computed;
        private List<EquipEntry> _equipEntries;

        // Cached filtered sim list — rebuilt once per frame to stay stable across IMGUI Layout/Repaint
        private readonly List<SimPlayerTracking> _filteredSims = new List<SimPlayerTracking>();
        private string _lastAppliedFilter;
        private int _lastFilterFrame = -1;

        // Hotkey
        private KeyCode _toggleKey = KeyCode.F8;

        // Harmony
        private Harmony _harmony;

        // IMGUI styles (initialized once)
        private GUIStyle _windowStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _statLabelStyle;
        private GUIStyle _statValueStyle;
        private GUIStyle _simButtonStyle;
        private GUIStyle _simButtonGreenStyle;
        private GUIStyle _itemClickStyle;
        private GUIStyle _itemClickBlessedStyle;
        private GUIStyle _itemClickGodlyStyle;
        private GUIStyle _itemClickEnhancedStyle;
        private GUIStyle _resizeHandleStyle;
        private Texture2D _hoverBgTex;
        private bool _stylesInit;

        private struct ComputedStats
        {
            public int Str, End, Dex, Agi, Int, Wis, Cha;
            public int HP, Mana, AC;
            public int MR, ER, PR, VR;
            public int HPRegen, MPRegen;
            public int AtkBonus;
            public int StrScale, EndScale, DexScale, AgiScale, IntScale, WisScale, ChaScale;
            public string ClassName;
            public int Level;
        }

        private struct EquipEntry
        {
            public Item Item;
            public int Quality;
            public string SlotLabel;
        }

        private void Awake()
        {
            Log.Info = s => Logging.LogInfo(s);
            Log.Warning = s => Logging.LogWarning(s);
            Log.Error = s => Logging.LogError(s);

            Settings = Config.Register<LunarisConfig>().Get();

            if (!string.IsNullOrEmpty(Settings.ToggleKey) &&
                Enum.TryParse(Settings.ToggleKey, true, out KeyCode parsed))
            {
                _toggleKey = parsed;
            }

            _harmony = new Harmony("com.siminspect");
            _harmony.PatchAll();

            Log.Info("Sim Inspector loaded. Press " + _toggleKey + " to toggle.");
        }

        private void Update()
        {
            if (GameData.SimMngr == null) return;

            // Keep static copies in sync for the Harmony patch
            S_ShowSelector = _showSelector;
            S_ShowInspect = _showInspect && _inspectedData != null;
            S_SelectorRect = _selectorRect;
            S_InspectRect = _inspectRect;

            // Input blocking must run every frame regardless of PlayerTyping,
            // otherwise the camera moves when mousing over our windows while
            // the search field has focus.
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = _isResizingSelector || _isResizingInspect;
            if (_showSelector && _selectorRect.Contains(mouse))
                over = true;
            if (_showInspect && _inspectedData != null && _inspectRect.Contains(mouse))
                over = true;

            // Block game camera rotation/zoom using the game's own drag flag
            if (over)
            {
                GameData.DraggingUIElement = true;
                _ownsDragFlag = true;
            }
            else if (_ownsDragFlag)
            {
                GameData.DraggingUIElement = false;
                _ownsDragFlag = false;
            }

            if (GameData.PlayerTyping) return;

            if (Input.GetKeyDown(_toggleKey))
            {
                _showSelector = !_showSelector;
                if (!_showSelector)
                {
                    _showInspect = false;
                }
            }
        }

        private void OnGUI()
        {
            if (!_showSelector && !_showInspect) return;
            if (GameData.SimMngr == null) return;

            InitStyles();

            // Draw solid hover backgrounds behind windows when mouse is over them
            Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

            if (_showSelector)
            {
                HandleResize(ref _selectorRect, ref _isResizingSelector);

                if (_selectorRect.Contains(guiMouse) && _hoverBgTex != null)
                {
                    GUI.DrawTexture(_selectorRect, _hoverBgTex);
                }

                _selectorRect = GUILayout.Window(
                    9823401, _selectorRect, DrawSelectorWindow, "Sim Inspector",
                    _windowStyle, GUILayout.Width(_selectorRect.width), GUILayout.Height(_selectorRect.height));
            }

            if (_showInspect && _inspectedData != null)
            {
                HandleResize(ref _inspectRect, ref _isResizingInspect);

                if (_inspectRect.Contains(guiMouse) && _hoverBgTex != null)
                {
                    GUI.DrawTexture(_inspectRect, _hoverBgTex);
                }

                _inspectRect = GUILayout.Window(
                    9823402, _inspectRect, DrawInspectWindow,
                    _inspectedName + " — Inspect",
                    _windowStyle, GUILayout.Width(_inspectRect.width), GUILayout.Height(_inspectRect.height));
            }
        }

        // ─── Resize Handling ──────────────────────────────────────────────

        /// <summary>
        /// Call from OnGUI() outside window functions. Uses screen-space coordinates.
        /// Draws a resize grip and handles drag-to-resize for the given window rect.
        /// </summary>
        private void HandleResize(ref Rect windowRect, ref bool isResizing)
        {
            Rect handleRect = new Rect(
                windowRect.xMax - ResizeHandleSize,
                windowRect.yMax - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);

            // Draw resize grip indicator
            if (Event.current.type == EventType.Repaint)
            {
                GUI.Label(handleRect, "◢", _resizeHandleStyle);
            }

            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizing = true;
                Event.current.Use();
            }

            if (isResizing)
            {
                if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
                {
                    float newW = Mathf.Max(MinWindowWidth, Event.current.mousePosition.x - windowRect.x);
                    float newH = Mathf.Max(MinWindowHeight, Event.current.mousePosition.y - windowRect.y);
                    windowRect.width = newW;
                    windowRect.height = newH;
                }

                if (Event.current.type == EventType.MouseUp)
                {
                    isResizing = false;
                }

                if (Event.current.type == EventType.MouseDrag)
                {
                    Event.current.Use();
                }
            }
        }

        // ─── Windows ──────────────────────────────────────────────────────

        private void DrawSelectorWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", GUILayout.Width(50));
            _searchFilter = GUILayout.TextField(_searchFilter);
            if (GUILayout.Button("X", GUILayout.Width(22)))
            {
                _searchFilter = "";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            string currentScene = SceneManager.GetActiveScene().name;
            string filter = _searchFilter?.Trim().ToLowerInvariant() ?? "";

            // Rebuild the filtered list once per frame so it stays stable
            // across IMGUI Layout and Repaint events (the live Sims list can
            // shift between passes, causing labels to land on wrong slots).
            int frame = Time.frameCount;
            if (frame != _lastFilterFrame || filter != _lastAppliedFilter)
            {
                _lastFilterFrame = frame;
                _lastAppliedFilter = filter;
                _filteredSims.Clear();

                var sims = GameData.SimMngr.Sims;
                for (int i = 0; i < sims.Count; i++)
                {
                    var sim = sims[i];
                    if (sim == null) continue;
                    if (string.IsNullOrEmpty(sim.SimName)) continue;

                    if (filter.Length > 0)
                    {
                        string label = $"{sim.SimName} - Lvl {sim.Level} {sim.ClassName} [{sim.CurScene}]";
                        if (!label.ToLowerInvariant().Contains(filter))
                            continue;
                    }

                    _filteredSims.Add(sim);
                }
            }

            _selectorScroll = GUILayout.BeginScrollView(_selectorScroll);

            for (int i = 0; i < _filteredSims.Count; i++)
            {
                var sim = _filteredSims[i];
                string label = $"{sim.SimName} - Lvl {sim.Level} {sim.ClassName} [{sim.CurScene}]";
                bool inZone = sim.CurScene == currentScene;
                GUIStyle btnStyle = inZone ? _simButtonGreenStyle : _simButtonStyle;

                if (GUILayout.Button(label, btnStyle))
                {
                    OpenInspect(sim.SimName);
                }
            }

            GUILayout.EndScrollView();

            // Consume any remaining scroll events so camera doesn't zoom
            if (Event.current.type == EventType.ScrollWheel)
                Event.current.Use();

            GUILayout.Space(4);
            if (GUILayout.Button("Close"))
            {
                _showSelector = false;
                _showInspect = false;
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void OpenInspect(string simName)
        {
            try
            {
                var data = SimPlayerDataManager.GetMyData(simName);
                if (data == null)
                {
                    Log.Warning("No save data found for sim: " + simName);
                    return;
                }

                _inspectedData = data;
                _inspectedName = simName;
                _equipEntries = BuildEquipList(data);
                _computed = ComputeStats(data, _equipEntries);
                _showInspect = true;
                _inspectScroll = Vector2.zero;
            }
            catch (Exception ex)
            {
                Log.Error("Failed to inspect sim " + simName + ": " + ex);
            }
        }

        private void DrawInspectWindow(int id)
        {
            _inspectScroll = GUILayout.BeginScrollView(_inspectScroll);

            // Header
            GUILayout.Label(
                $"Level {_computed.Level} {_computed.ClassName}",
                _headerStyle);
            GUILayout.Space(6);

            // Equipment
            GUILayout.Label("─── Equipment ───", _headerStyle);
            GUILayout.Space(2);

            if (_equipEntries != null)
            {
                foreach (var entry in _equipEntries)
                {
                    if (entry.Item == null) continue;

                    GUILayout.BeginHorizontal();

                    // Determine quality name and style
                    string itemName = entry.Item.ItemName;
                    GUIStyle clickStyle;
                    if (entry.Quality == 2)
                    {
                        clickStyle = _itemClickBlessedStyle;
                    }
                    else if (entry.Quality == 3)
                    {
                        clickStyle = _itemClickGodlyStyle;
                    }
                    else if (entry.Quality > 10)
                    {
                        itemName = $"{entry.Item.ItemName} +{entry.Quality - 10}";
                        clickStyle = _itemClickEnhancedStyle;
                    }
                    else
                    {
                        clickStyle = _itemClickStyle;
                    }

                    // Icon — clickable
                    bool iconClicked = false;
                    if (entry.Item.ItemIcon != null)
                    {
                        var sprite = entry.Item.ItemIcon;
                        var tex = sprite.texture;
                        var rect = sprite.textureRect;
                        Rect uvRect = new Rect(
                            rect.x / tex.width,
                            rect.y / tex.height,
                            rect.width / tex.width,
                            rect.height / tex.height);
                        Rect iconArea = GUILayoutUtility.GetRect(28, 28,
                            GUILayout.Width(28), GUILayout.Height(28));

                        GUI.DrawTextureWithTexCoords(iconArea, tex, uvRect);

                        // Check for click on icon area
                        if (Event.current.type == EventType.MouseDown
                            && Event.current.button == 0
                            && iconArea.Contains(Event.current.mousePosition))
                        {
                            iconClicked = true;
                            Event.current.Use();
                        }
                    }

                    // Name — clickable label (styled as button-like)
                    string displayText = $"[{entry.SlotLabel}] {itemName}";
                    if (GUILayout.Button(displayText, clickStyle))
                    {
                        ShowGameItemInfo(entry.Item, entry.Quality);
                    }

                    if (iconClicked)
                    {
                        ShowGameItemInfo(entry.Item, entry.Quality);
                    }

                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(8);

            // Two-column stat layout
            GUILayout.BeginHorizontal();

            // ── Left column: Attributes + Derived Stats ──
            GUILayout.BeginVertical();

            GUILayout.Label("─── Attributes ───", _headerStyle);
            GUILayout.Space(2);
            DrawStatRow("STR", _computed.Str);
            DrawStatRow("END", _computed.End);
            DrawStatRow("DEX", _computed.Dex);
            DrawStatRow("AGI", _computed.Agi);
            DrawStatRow("INT", _computed.Int);
            DrawStatRow("WIS", _computed.Wis);
            DrawStatRow("CHA", _computed.Cha);

            GUILayout.Space(8);
            GUILayout.Label("─── Derived Stats ───", _headerStyle);
            GUILayout.Space(2);
            DrawStatRow("HP", _computed.HP);
            DrawStatRow("Mana", _computed.Mana);
            DrawStatRow("AC", _computed.AC);
            DrawStatRow("HP Regen", _computed.HPRegen);
            DrawStatRow("MP Regen", _computed.MPRegen);
            DrawStatRow("Atk Bonus", _computed.AtkBonus);

            GUILayout.EndVertical();

            GUILayout.Space(12);

            // ── Right column: Resistances + Proficiencies ──
            GUILayout.BeginVertical();

            GUILayout.Label("─── Resistances ───", _headerStyle);
            GUILayout.Space(2);
            DrawStatRow("Magic", _computed.MR, "%");
            DrawStatRow("Elemental", _computed.ER, "%");
            DrawStatRow("Poison", _computed.PR, "%");
            DrawStatRow("Void", _computed.VR, "%");

            GUILayout.Space(8);
            GUILayout.Label("─── Proficiencies ───", _headerStyle);
            GUILayout.Space(2);
            DrawStatRow("STR Scale", _computed.StrScale);
            DrawStatRow("END Scale", _computed.EndScale);
            DrawStatRow("DEX Scale", _computed.DexScale);
            DrawStatRow("AGI Scale", _computed.AgiScale);
            DrawStatRow("INT Scale", _computed.IntScale);
            DrawStatRow("WIS Scale", _computed.WisScale);
            DrawStatRow("CHA Scale", _computed.ChaScale);

            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();

            // Consume any remaining scroll events so camera doesn't zoom
            if (Event.current.type == EventType.ScrollWheel)
                Event.current.Use();

            GUILayout.Space(4);
            if (GUILayout.Button("Close"))
            {
                _showInspect = false;
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void ShowGameItemInfo(Item item, int quality)
        {
            if (GameData.ItemInfoWindow == null) return;
            try
            {
                // Close any existing item window first — DisplayItem() has an early
                // return if ParentWindow is already active
                GameData.ItemInfoWindow.CloseItemWindow();
                GameData.ItemInfoWindow.DisplayItem(item, Vector2.zero, quality);
                RepositionItemInfoWindow();
            }
            catch (Exception ex)
            {
                Log.Warning("Could not display item info: " + ex.Message);
            }
        }

        /// <summary>
        /// Moves the game's ItemInfoWindow so it doesn't overlap our inspect window.
        /// Places it to the right of the inspect rect, or to the left if not enough room.
        /// </summary>
        private void RepositionItemInfoWindow()
        {
            try
            {
                var parentWindow = GameData.ItemInfoWindow.ParentWindow;
                if (parentWindow == null || !parentWindow.activeSelf) return;

                var windowRT = parentWindow.GetComponent<RectTransform>();
                if (windowRT == null) return;

                // Find the root canvas RectTransform for coordinate conversion
                var canvas = windowRT.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                var canvasRT = canvas.GetComponent<RectTransform>();
                if (canvasRT == null) return;

                // Convert our IMGUI inspect rect to screen coordinates
                // IMGUI Y is top-down, screen Y is bottom-up
                float inspectScreenRight = _inspectRect.xMax;
                float inspectScreenLeft = _inspectRect.x;

                // Convert inspect rect edges to canvas-local space
                Vector2 rightEdgeScreen = new Vector2(inspectScreenRight + 10f, Screen.height / 2f);
                Vector2 leftEdgeScreen = new Vector2(inspectScreenLeft - 10f, Screen.height / 2f);

                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRT, rightEdgeScreen, null, out Vector2 rightLocal);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRT, leftEdgeScreen, null, out Vector2 leftLocal);

                // Get the item window's size to check if it fits
                Vector2 windowSize = windowRT.rect.size;
                float halfWidth = windowSize.x * 0.5f;
                Rect canvasRect = canvasRT.rect;

                // Try placing to the right of our inspect window
                float targetX = rightLocal.x + halfWidth;
                if (targetX + halfWidth > canvasRect.xMax - 6f)
                {
                    // Doesn't fit on right — place to the left
                    targetX = leftLocal.x - halfWidth;
                }

                // Keep current Y position (already set by DisplayItem)
                Vector2 pos = windowRT.anchoredPosition;
                pos.x = targetX;

                // Clamp inside canvas
                pos.x = Mathf.Clamp(pos.x, canvasRect.xMin + halfWidth + 6f, canvasRect.xMax - halfWidth - 6f);
                windowRT.anchoredPosition = pos;
            }
            catch (Exception ex)
            {
                Log.Warning("Could not reposition item info: " + ex.Message);
            }
        }

        private void DrawStatRow(string label, int value, string suffix = "")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _statLabelStyle, GUILayout.Width(100));
            GUILayout.Label(value + suffix, _statValueStyle);
            GUILayout.EndHorizontal();
        }

        // ─── Data Processing ──────────────────────────────────────────────

        private List<EquipEntry> BuildEquipList(SimPlayerSaveData data)
        {
            var list = new List<EquipEntry>();
            if (data.MyEquippedItems == null || GameData.ItemDB == null) return list;

            var empty = GameData.PlayerInv?.Empty;

            for (int i = 0; i < data.MyEquippedItems.Count; i++)
            {
                string itemId = data.MyEquippedItems[i];
                if (string.IsNullOrEmpty(itemId)) continue;

                Item item = GameData.ItemDB.GetItemByID(itemId);
                if (item == null || item == empty) continue;

                int qual = (i < data.ItemQuantities.Count) ? data.ItemQuantities[i] : 1;
                string slotLabel = item.RequiredSlot.ToString();

                list.Add(new EquipEntry
                {
                    Item = item,
                    Quality = qual,
                    SlotLabel = slotLabel
                });
            }

            return list;
        }

        private ComputedStats ComputeStats(SimPlayerSaveData data, List<EquipEntry> equipList)
        {
            var stats = new ComputedStats();
            stats.Level = data.MyLevel;

            // Resolve class
            Class cls = ResolveClass(data);
            stats.ClassName = cls != null ? cls.DisplayName : "Unknown";

            // Sum gear stats from the already-resolved equip list
            int itemStr = 0, itemEnd = 0, itemDex = 0, itemAgi = 0;
            int itemInt = 0, itemWis = 0, itemCha = 0;
            int itemHP = 0, itemMana = 0, itemAC = 0;
            int itemMR = 0, itemER = 0, itemPR = 0, itemVR = 0;
            float charmStrScale = 0, charmEndScale = 0, charmDexScale = 0, charmAgiScale = 0;
            float charmIntScale = 0, charmWisScale = 0, charmChaScale = 0;
            float charmMitScale = 0;

            foreach (var entry in equipList)
            {
                var item = entry.Item;
                if (item == null) continue;
                int qual = entry.Quality;

                if (item.RequiredSlot == Item.SlotType.Charm)
                {
                    charmStrScale = item.StrScaling;
                    charmEndScale = item.EndScaling;
                    charmDexScale = item.DexScaling;
                    charmAgiScale = item.AgiScaling;
                    charmIntScale = item.IntScaling;
                    charmWisScale = item.WisScaling;
                    charmChaScale = item.ChaScaling;
                    charmMitScale = item.MitigationScaling;
                    continue;
                }

                if (item.RequiredSlot == Item.SlotType.Aura ||
                    item.RequiredSlot == Item.SlotType.General)
                    continue;

                itemStr += item.CalcStat(item.Str, qual);
                itemEnd += item.CalcStat(item.End, qual);
                itemDex += item.CalcStat(item.Dex, qual);
                itemAgi += item.CalcStat(item.Agi, qual);
                itemInt += item.CalcStat(item.Int, qual);
                itemWis += item.CalcStat(item.Wis, qual);
                itemCha += item.CalcStat(item.Cha, qual);
                itemHP += item.CalcACHPMC(item.HP, qual);
                itemMana += item.CalcACHPMC(item.Mana, qual);
                itemAC += item.CalcAC(item.AC, qual);
                itemMR += item.CalcResists(item.MR, qual);
                itemER += item.CalcResists(item.ER, qual);
                itemPR += item.CalcResists(item.PR, qual);
                itemVR += item.CalcResists(item.VR, qual);
            }

            // Base stats (Unity serialized defaults = 12 for all)
            const int baseStat = 12;
            const int baseHP = 100;
            const int baseMana = 50;
            const int baseAC = 0;
            const int baseResist = 5;

            // Current stats (no buffs)
            stats.Str = baseStat + itemStr;
            stats.End = baseStat + itemEnd;
            stats.Dex = baseStat + itemDex;
            stats.Agi = baseStat + itemAgi;
            stats.Int = baseStat + itemInt;
            stats.Wis = baseStat + itemWis;
            stats.Cha = baseStat + itemCha;

            // Proficiency scaling
            int clsStr = cls != null ? cls.StrBenefit : 0;
            int clsEnd = cls != null ? cls.EndBenefit : 0;
            int clsDex = cls != null ? cls.DexBenefit : 0;
            int clsAgi = cls != null ? cls.AgiBenefit : 0;
            int clsInt = cls != null ? cls.IntBenefit : 0;
            int clsWis = cls != null ? cls.WisBenefit : 0;
            int clsCha = cls != null ? cls.ChaBenefit : 0;

            stats.StrScale = Mathf.Clamp(clsStr + (int)charmStrScale + data.StrPointsSpent, 1, 40);
            stats.EndScale = Mathf.Clamp(clsEnd + (int)charmEndScale + data.EndPointsSpent, 1, 40);
            stats.DexScale = Mathf.Clamp(clsDex + (int)charmDexScale + data.DexPointsSpent, 1, 40);
            stats.AgiScale = Mathf.Clamp(clsAgi + (int)charmAgiScale + data.AgiPointsSpent, 1, 40);
            stats.IntScale = Mathf.Clamp(clsInt + (int)charmIntScale + data.IntPointsSpent, 1, 40);
            stats.WisScale = Mathf.Clamp(clsWis + (int)charmWisScale + data.WisPointsSpent, 1, 40);
            stats.ChaScale = Mathf.Clamp(clsCha + (int)charmChaScale + data.ChaPointsSpent, 1, 40);

            // HP: (BaseHP + ItemHP + Level*5 + (END * (2*EndScale) / 100 + END * RoundToInt(Level/200)) * Level)
            int endHPContrib = (stats.End * (2 * stats.EndScale) / 100
                + stats.End * Mathf.RoundToInt((float)stats.Level / 200f)) * stats.Level;
            stats.HP = Mathf.RoundToInt(baseHP + itemHP + stats.Level * 5 + endHPContrib);

            // Mana: BaseMana + ItemMana + IntScale*Level + WisScale*Level + RoundToInt(INT * IntScale/3)
            stats.Mana = baseMana + itemMana
                + stats.IntScale * stats.Level
                + stats.WisScale * stats.Level
                + Mathf.RoundToInt(stats.Int * (stats.IntScale / 3f));

            // AC: RoundToInt((BaseAC + ItemAC + RoundToInt(AGI * AgiScale / 200 * Level)) * MitigationBonus)
            float mitBonus = cls != null ? cls.MitigationBonus : 1f;
            // Add charm mitigation scaling
            mitBonus += charmMitScale / 100f;
            int agiACContrib = Mathf.RoundToInt(stats.Agi * stats.AgiScale / 200f * stats.Level);
            stats.AC = Mathf.RoundToInt((baseAC + itemAC + agiACContrib) * mitBonus);

            // Resistances: BaseResist + ItemResist + (ChaScale / 100) * CHA (integer division)
            int chaResContrib = (stats.ChaScale / 100) * stats.Cha;
            stats.MR = baseResist + itemMR + chaResContrib;
            stats.ER = baseResist + itemER + chaResContrib;
            stats.PR = baseResist + itemPR + chaResContrib;
            stats.VR = baseResist + itemVR + chaResContrib;

            // HP Regen: 2 * (Level + RoundToInt((2 * EndScale) / 100 * END))
            stats.HPRegen = Mathf.RoundToInt(
                2 * (stats.Level + Mathf.RoundToInt(2f * stats.EndScale / 100f * stats.End)));

            // MP Regen: RoundToInt(WisScale / 140 * WIS) (no buff component)
            stats.MPRegen = Mathf.RoundToInt(stats.WisScale / 140f * stats.Wis);

            // Attack Bonus: Level * 10 + Dex * 1.5 + Dex * DexScale / 10
            stats.AtkBonus = Mathf.RoundToInt(
                stats.Level * 10f + stats.Dex * 1.5f + stats.Dex * (stats.DexScale / 10f));

            return stats;
        }

        private Class ResolveClass(SimPlayerSaveData data)
        {
            if (GameData.ClassDB == null) return null;

            if (data.Arc) return GameData.ClassDB.Arcanist;
            if (data.War) return GameData.ClassDB.Paladin; // War = Paladin in save data
            if (data.Dru) return GameData.ClassDB.Druid;
            if (data.Duel) return GameData.ClassDB.Duelist;
            if (data.Storm) return GameData.ClassDB.Stormcaller;
            if (data.Reav) return GameData.ClassDB.Reaver;

            return GameData.ClassDB.Default;
        }

        // ─── Style Initialization ─────────────────────────────────────────

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            // Create a solid slate/blue-gray background texture for hover state
            _hoverBgTex = new Texture2D(1, 1);
            _hoverBgTex.SetPixel(0, 0, new Color(0.18f, 0.22f, 0.28f, 0.95f));
            _hoverBgTex.Apply();

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                fontSize = 13
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 13,
                normal = { textColor = new Color(0.78f, 0.66f, 0.30f) } // gold
            };

            _statLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            _statValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _simButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };

            _simButtonGreenStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.green }
            };

            // Clickable item styles — look like labels but act as buttons
            _itemClickStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                hover = { textColor = Color.yellow },
                active = { textColor = Color.yellow }
            };
            // Copy hover/active backgrounds from button for visual feedback
            _itemClickStyle.hover.background = GUI.skin.button.hover.background;
            _itemClickStyle.active.background = GUI.skin.button.active.background;

            _itemClickBlessedStyle = new GUIStyle(_itemClickStyle)
            {
                normal = { textColor = new Color(0.33f, 0.53f, 0.80f) }
            };

            _itemClickGodlyStyle = new GUIStyle(_itemClickStyle)
            {
                normal = { textColor = new Color(0.75f, 0.45f, 1.0f) } // purple
            };

            _itemClickEnhancedStyle = new GUIStyle(_itemClickStyle)
            {
                normal = { textColor = Color.green }
            };

            _resizeHandleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }
            };
        }

        private void OnDestroy()
        {
            _showSelector = false;
            _showInspect = false;
            _inspectedData = null;
            _equipEntries = null;
            _filteredSims.Clear();
            _lastFilterFrame = -1;
            _stylesInit = false;
            S_ShowSelector = false;
            S_ShowInspect = false;

            if (_ownsDragFlag)
            {
                GameData.DraggingUIElement = false;
                _ownsDragFlag = false;
            }

            if (_hoverBgTex != null)
            {
                Destroy(_hoverBgTex);
                _hoverBgTex = null;
            }

            _harmony?.UnpatchSelf();
        }

        // ─── Harmony Patch ────────────────────────────────────────────────
        // Override IsPointerOverGameObject to return true when mouse is
        // over our IMGUI windows. This blocks camera rotation, zoom, and
        // click-through since the game already checks this in
        // CameraController.Controls() and PlayerControl.

        [HarmonyPatch(typeof(EventSystem), nameof(EventSystem.IsPointerOverGameObject), new Type[0])]
        private static class Patch_IsPointerOverGameObject
        {
            static void Postfix(ref bool __result)
            {
                if (__result) return;

                // Compute mouse-over directly to avoid frame-ordering issues
                // (CameraController.Update may run before our Update)
                if (!S_ShowSelector && !S_ShowInspect) return;

                Vector2 mouse = new Vector2(
                    Input.mousePosition.x,
                    Screen.height - Input.mousePosition.y);

                if (S_ShowSelector && S_SelectorRect.Contains(mouse))
                {
                    __result = true;
                    return;
                }
                if (S_ShowInspect && S_InspectRect.Contains(mouse))
                {
                    __result = true;
                }
            }
        }
    }
}
