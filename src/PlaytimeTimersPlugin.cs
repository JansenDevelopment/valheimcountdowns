using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Martijn.PlaytimeTimers;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class PlaytimeTimersPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "martijn.playtimetimers";
    public const string PluginName = "Playtime Timers";
    public const string PluginVersion = "1.3.0";

    private const float SaveIntervalSeconds = 10f;
    private const int HudWindowId = 872641;

    private readonly List<PlayTimer> _timers = new();

    private ConfigEntry<KeyboardShortcut> _toggleWindowKey = null!;
    private ConfigEntry<string> _serializedTimers = null!;
    private ConfigEntry<bool> _autoStartRoyalJellyOnMineExit = null!;
    private ConfigEntry<bool> _showHudWhenMenuClosed = null!;
    private ConfigEntry<bool> _hudMovable = null!;
    private ConfigEntry<bool> _showHudBackground = null!;
    private ConfigEntry<float> _hudBackgroundOpacity = null!;
    private ConfigEntry<float> _hudPositionX = null!;
    private ConfigEntry<float> _hudPositionY = null!;

    private Rect _windowRect = new(200f, 120f, 560f, 480f);
    private Rect _hudRect = new(20f, 20f, 440f, 300f);
    private Vector2 _scrollPosition;
    private bool _showWindow;
    private float _saveAccumulator;
    private bool _isInGameplayWorld;
    private List<PlayTimer> _hudVisibleTimers = new();

    private string _newTimerName = "New Timer";
    private string _newTimerMinutes = "20";
    private string _newTimerSeconds = "0";

    private bool _lastInInterior;
    private bool _insideInfestedMine;
    private string _lastInfestedMineScene = string.Empty;
    private Vector3 _lastInfestedMinePosition = Vector3.zero;
    private bool _hasLastInfestedMinePosition;

    private const string RoyalJellyTimerName = "Royal Jelly (Infested Mine)";
    private const float RoyalJellyRespawnSeconds = 4f * 60f * 60f;
    private const float MineCoordinateGridSize = 5f;

    private void Awake()
    {
        _toggleWindowKey = Config.Bind("General", "Toggle Window", new KeyboardShortcut(KeyCode.F8), "Open/close timer window.");
        _autoStartRoyalJellyOnMineExit = Config.Bind("General", "Auto Start Royal Jelly On Mine Exit", true, "Automatically adds/starts a 4h Royal Jelly timer for an Infested Mine location when you leave it (no duplicate location timers).");
        _showHudWhenMenuClosed = Config.Bind("General", "Show HUD When Menu Closed", true, "Show running timers on HUD while the F8 timer menu is closed.");
        _hudMovable = Config.Bind("General", "HUD Movable", false, "Allow dragging the HUD. If disabled, HUD stays at top-left.");
        _showHudBackground = Config.Bind("General", "HUD Show Background", true, "Show a background panel behind the HUD text.");
        _hudBackgroundOpacity = Config.Bind("General", "HUD Background Opacity", 0.45f, new ConfigDescription("HUD background opacity (0 = transparent, 1 = solid).", new AcceptableValueRange<float>(0f, 1f)));
        _hudPositionX = Config.Bind("General", "HUD Position X", 20f, "HUD position X in pixels.");
        _hudPositionY = Config.Bind("General", "HUD Position Y", 20f, "HUD position Y in pixels.");
        _serializedTimers = Config.Bind("Storage", "Timers", string.Empty, "Internal timer storage. Do not edit manually.");

        _hudRect.x = Mathf.Max(0f, _hudPositionX.Value);
        _hudRect.y = Mathf.Max(0f, _hudPositionY.Value);
        _hudBackgroundOpacity.Value = Mathf.Clamp01(_hudBackgroundOpacity.Value);
        ClampHudRectToScreen();

        LoadTimers();
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Press {_toggleWindowKey.Value} to open timer UI.");
    }

    private void OnDestroy()
    {
        SaveTimers();
    }

    private void OnApplicationQuit()
    {
        SaveTimers();
    }

    private void Update()
    {
        if (_toggleWindowKey.Value.IsDown())
        {
            _showWindow = !_showWindow;
        }

        var inMainMenu = IsMainMenuScene();
        var worldSeconds = 0d;
        var hasWorldTime = !inMainMenu && TryGetWorldTimeSeconds(out worldSeconds);
        _isInGameplayWorld = !inMainMenu && (hasWorldTime || HasLocalPlayer());

        if (_isInGameplayWorld)
        {
            HandleAutoStartRoyalJellyTimer();
        }
        else
        {
            ResetMineTracking();
        }

        if (hasWorldTime)
        {
            foreach (var timer in _timers)
            {
                if (!timer.IsRunning || timer.RemainingSeconds <= 0f)
                {
                    continue;
                }

                if (!timer.UseWorldClock)
                {
                    timer.UseWorldClock = true;
                }

                if (timer.WorldTargetSeconds <= 0d)
                {
                    timer.WorldTargetSeconds = worldSeconds + timer.RemainingSeconds;
                }

                timer.RemainingSeconds = Mathf.Max(0f, (float)(timer.WorldTargetSeconds - worldSeconds));

                if (timer.RemainingSeconds <= 0f)
                {
                    timer.IsRunning = false;
                    if (!timer.HasFinishedNotification)
                    {
                        timer.HasFinishedNotification = true;
                        Logger.LogMessage($"[PlaytimeTimers] Timer finished: {timer.Name}");
                    }
                }
            }
        }

        _saveAccumulator += Time.unscaledDeltaTime;
        if (_saveAccumulator >= SaveIntervalSeconds)
        {
            _saveAccumulator = 0f;
            SaveTimers();
        }
    }

    private void OnGUI()
    {
        if (!_showWindow)
        {
            if (_showHudWhenMenuClosed.Value)
            {
                DrawHudOverlay();
            }

            return;
        }

        _windowRect = GUILayout.Window(GetInstanceID(), _windowRect, DrawWindow, "Playtime Timers");
    }

    private void DrawWindow(int id)
    {
        GUILayout.BeginVertical();

        GUILayout.Label("Timers store a server/world-time target and compare against current server/world time.");

        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(260));
        foreach (var timer in _timers.ToList())
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label($"{timer.Name}");
            GUILayout.Label($"Remaining: {FormatTime(timer.RemainingSeconds)} / {FormatTime(timer.DurationSeconds)}");
            GUILayout.Label(timer.UseWorldClock ? "Clock: Server/World (timestamp)" : "Clock: Waiting for world time");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(timer.IsRunning ? "Pause" : "Start", GUILayout.Width(80)))
            {
                if (timer.RemainingSeconds <= 0f)
                {
                    timer.RemainingSeconds = timer.DurationSeconds;
                    timer.HasFinishedNotification = false;
                }

                if (timer.IsRunning)
                {
                    timer.IsRunning = false;
                }
                else
                {
                    StartTimer(timer);
                }
            }

            if (GUILayout.Button("Reset", GUILayout.Width(80)))
            {
                timer.RemainingSeconds = timer.DurationSeconds;
                timer.IsRunning = false;
                timer.HasFinishedNotification = false;
                timer.WorldTargetSeconds = 0d;
            }

            if (GUILayout.Button("+1m", GUILayout.Width(80)))
            {
                timer.DurationSeconds += 60f;
                timer.RemainingSeconds += 60f;
                if (timer.IsRunning)
                {
                    if (timer.WorldTargetSeconds > 0d)
                    {
                        timer.WorldTargetSeconds += 60d;
                    }
                    else if (TryGetWorldTimeSeconds(out var worldNowPlus))
                    {
                        timer.UseWorldClock = true;
                        timer.WorldStartSeconds = worldNowPlus;
                        timer.WorldTargetSeconds = worldNowPlus + timer.RemainingSeconds;
                    }
                }
                timer.HasFinishedNotification = false;
            }

            if (GUILayout.Button("Delete", GUILayout.Width(80)))
            {
                _timers.Remove(timer);
                SaveTimers();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                continue;
            }

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        GUILayout.EndScrollView();

        GUILayout.Space(8);
        GUILayout.Label("Create Timer");

        _showHudWhenMenuClosed.Value = GUILayout.Toggle(_showHudWhenMenuClosed.Value, "Show HUD when menu is closed");
        var hudMovable = GUILayout.Toggle(_hudMovable.Value, "Allow moving HUD");
        if (hudMovable != _hudMovable.Value)
        {
            _hudMovable.Value = hudMovable;
            if (!_hudMovable.Value)
            {
                _hudRect.x = 20f;
                _hudRect.y = 20f;
                SaveHudPosition();
            }
        }
        _showHudBackground.Value = GUILayout.Toggle(_showHudBackground.Value, "Show HUD background panel");

        GUILayout.BeginHorizontal();
        GUILayout.Label($"HUD opacity: {Mathf.RoundToInt(_hudBackgroundOpacity.Value * 100f)}%", GUILayout.Width(180));
        var hudOpacity = GUILayout.HorizontalSlider(_hudBackgroundOpacity.Value, 0f, 1f, GUILayout.Width(180));
        if (Math.Abs(hudOpacity - _hudBackgroundOpacity.Value) > 0.001f)
        {
            _hudBackgroundOpacity.Value = Mathf.Clamp01(hudOpacity);
        }

        if (GUILayout.Button("Reset HUD Pos", GUILayout.Width(120)))
        {
            _hudRect.x = 20f;
            _hudRect.y = 20f;
            SaveHudPosition();
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", GUILayout.Width(60));
        _newTimerName = GUILayout.TextField(_newTimerName, GUILayout.Width(220));
        GUILayout.Label("Min", GUILayout.Width(40));
        _newTimerMinutes = GUILayout.TextField(_newTimerMinutes, GUILayout.Width(50));
        GUILayout.Label("Sec", GUILayout.Width(40));
        _newTimerSeconds = GUILayout.TextField(_newTimerSeconds, GUILayout.Width(50));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Add (Paused)", GUILayout.Width(120)))
        {
            AddTimer(startNow: false);
        }

        if (GUILayout.Button("Add + Start", GUILayout.Width(120)))
        {
            AddTimer(startNow: true);
        }

        if (GUILayout.Button("Save Now", GUILayout.Width(100)))
        {
            SaveTimers();
        }

        if (GUILayout.Button("Royal Jelly 4h", GUILayout.Width(120)))
        {
            AddPresetRoyalJelly(startNow: true);
        }

        if (GUILayout.Button("Close", GUILayout.Width(80)))
        {
            _showWindow = false;
        }

        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    private void AddTimer(bool startNow)
    {
        var minutesParsed = int.TryParse(_newTimerMinutes, out var minutes);
        var secondsParsed = int.TryParse(_newTimerSeconds, out var seconds);
        if (!minutesParsed)
        {
            minutes = 0;
        }

        if (!secondsParsed)
        {
            seconds = 0;
        }

        var totalSeconds = Mathf.Max(1, (minutes * 60) + seconds);
        var name = string.IsNullOrWhiteSpace(_newTimerName) ? $"Timer {_timers.Count + 1}" : _newTimerName.Trim();

        var timer = new PlayTimer
        {
            Name = name,
            DurationSeconds = totalSeconds,
            RemainingSeconds = totalSeconds,
            IsRunning = false,
            HasFinishedNotification = false
        };

        if (startNow)
        {
            StartTimer(timer);
        }

        _timers.Add(timer);

        SaveTimers();
    }

    private void AddPresetRoyalJelly(bool startNow)
    {
        var timer = new PlayTimer
        {
            Name = RoyalJellyTimerName,
            DurationSeconds = RoyalJellyRespawnSeconds,
            RemainingSeconds = RoyalJellyRespawnSeconds,
            IsRunning = false,
            HasFinishedNotification = false,
            IsRoyalJellyAuto = false
        };

        if (startNow)
        {
            StartTimer(timer);
        }

        _timers.Add(timer);

        SaveTimers();
    }

    private void LoadTimers()
    {
        _timers.Clear();

        var raw = _serializedTimers.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var entries = raw.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split('|');
            if (parts.Length < 4)
            {
                continue;
            }

            var name = DecodeName(parts[0]);
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            {
                continue;
            }

            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var remaining))
            {
                continue;
            }

            var running = parts[3] == "1";
            var useWorldClock = parts.Length > 4 && parts[4] == "1";
            var worldStartSeconds = 0d;
            if (parts.Length > 5)
            {
                double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out worldStartSeconds);
            }
            var isRoyalAuto = parts.Length > 6 && parts[6] == "1";
            var locationKey = parts.Length > 7 ? DecodeName(parts[7]) : string.Empty;
            var worldTargetSeconds = 0d;
            if (parts.Length > 8)
            {
                double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out worldTargetSeconds);
            }
            else if (useWorldClock && worldStartSeconds > 0d)
            {
                worldTargetSeconds = worldStartSeconds + Mathf.Max(1f, duration);
            }

            _timers.Add(new PlayTimer
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Timer" : name,
                DurationSeconds = Mathf.Max(1f, duration),
                RemainingSeconds = Mathf.Clamp(remaining, 0f, Mathf.Max(1f, duration)),
                IsRunning = running && remaining > 0f,
                HasFinishedNotification = remaining <= 0f,
                UseWorldClock = useWorldClock,
                WorldStartSeconds = worldStartSeconds,
                WorldTargetSeconds = worldTargetSeconds,
                IsRoyalJellyAuto = isRoyalAuto,
                LocationKey = locationKey
            });
        }
    }

    private void SaveTimers()
    {
        var lines = _timers.Select(timer => string.Join("|", new[]
        {
            EncodeName(timer.Name),
            timer.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            timer.RemainingSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            timer.IsRunning ? "1" : "0",
            timer.UseWorldClock ? "1" : "0",
            timer.WorldStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            timer.IsRoyalJellyAuto ? "1" : "0",
            EncodeName(timer.LocationKey),
            timer.WorldTargetSeconds.ToString("0.###", CultureInfo.InvariantCulture)
        }));

        _serializedTimers.Value = string.Join(";;", lines);
        Config.Save();
    }

    private static string FormatTime(float totalSeconds)
    {
        var value = Mathf.Max(0, Mathf.RoundToInt(totalSeconds));
        var hours = value / 3600;
        var minutes = (value % 3600) / 60;
        var seconds = value % 60;

        return hours > 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }

    private static string EncodeName(string name)
    {
        var text = string.IsNullOrEmpty(name) ? "Timer" : name;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    private static string DecodeName(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return "Timer";
        }
    }

    private sealed class PlayTimer
    {
        public string Name = "Timer";
        public float DurationSeconds;
        public float RemainingSeconds;
        public bool IsRunning;
        public bool HasFinishedNotification;
        public bool UseWorldClock;
        public double WorldStartSeconds;
        public double WorldTargetSeconds;
        public bool IsRoyalJellyAuto;
        public string LocationKey = string.Empty;
    }

    private void StartTimer(PlayTimer timer)
    {
        timer.IsRunning = true;
        timer.HasFinishedNotification = false;
        timer.UseWorldClock = true;

        if (TryGetWorldTimeSeconds(out var worldNow))
        {
            timer.WorldStartSeconds = worldNow;
            timer.WorldTargetSeconds = worldNow + timer.RemainingSeconds;
        }
    }

    private void HandleAutoStartRoyalJellyTimer()
    {
        if (!_autoStartRoyalJellyOnMineExit.Value)
        {
            return;
        }

        var inInterior = IsPlayerInInterior();
        var sceneName = SceneManager.GetActiveScene().name;
        var environmentName = string.Empty;
        var hasEnvironmentName = TryGetCurrentEnvironmentName(out environmentName);

        var mineByScene = LooksLikeInfestedMineScene(sceneName);
        var mineByEnvironment = hasEnvironmentName && LooksLikeInfestedMineScene(environmentName);
        var inMineContext = mineByScene || mineByEnvironment || (inInterior && _insideInfestedMine);

        if (inMineContext)
        {
            _insideInfestedMine = true;

            var contextName = mineByScene
                ? sceneName
                : (mineByEnvironment ? environmentName : sceneName);

            if (!string.IsNullOrWhiteSpace(contextName))
            {
                _lastInfestedMineScene = contextName;
            }

            if (TryGetPlayerWorldPosition(out var playerPosition))
            {
                _lastInfestedMinePosition = playerPosition;
                _hasLastInfestedMinePosition = true;
            }
        }

        if (!inMineContext && _insideInfestedMine)
        {
            AddAutoRoyalJellyTimerForLocation();
            _insideInfestedMine = false;
            _lastInfestedMineScene = string.Empty;
            _hasLastInfestedMinePosition = false;
        }

        _lastInInterior = inInterior;
    }

    private void ResetMineTracking()
    {
        _insideInfestedMine = false;
        _lastInInterior = false;
        _lastInfestedMineScene = string.Empty;
        _hasLastInfestedMinePosition = false;
    }

    private static bool LooksLikeInfestedMineScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return false;
        }

        var text = sceneName.ToLowerInvariant();
        return text.Contains("infested") || text.Contains("infected") || text.Contains("mine") || text.Contains("dvergr");
    }

    private static bool TryGetCurrentEnvironmentName(out string environmentName)
    {
        environmentName = string.Empty;

        try
        {
            var envManType = Type.GetType("EnvMan, Assembly-CSharp");
            if (envManType == null)
            {
                return false;
            }

            var instanceProperty = envManType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var instanceField = envManType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var envManInstance = instanceProperty?.GetValue(null) ?? instanceField?.GetValue(null);
            if (envManInstance == null)
            {
                return false;
            }

            var getCurrentEnvironmentMethod = envManType.GetMethod("GetCurrentEnvironment", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var currentEnvironment = getCurrentEnvironmentMethod?.Invoke(envManInstance, null);
            if (currentEnvironment == null)
            {
                return false;
            }

            var envType = currentEnvironment.GetType();
            var nameField = envType.GetField("m_name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (nameField?.GetValue(currentEnvironment) is string fieldName && !string.IsNullOrWhiteSpace(fieldName))
            {
                environmentName = fieldName;
                return true;
            }

            var nameProperty = envType.GetProperty("m_name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? envType.GetProperty("name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (nameProperty?.GetValue(currentEnvironment) is string propertyName && !string.IsNullOrWhiteSpace(propertyName))
            {
                environmentName = propertyName;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void AddAutoRoyalJellyTimerForLocation()
    {
        if (!_hasLastInfestedMinePosition)
        {
            Logger.LogDebug("[PlaytimeTimers] Skipped Infested Mine auto-timer: no tracked mine position.");
            return;
        }

        var locationKey = BuildMineLocationKey(_lastInfestedMineScene, _lastInfestedMinePosition);
        var existingTimer = _timers.FirstOrDefault(t => t.IsRoyalJellyAuto && t.LocationKey == locationKey);
        if (existingTimer != null)
        {
            return;
        }

        var timerName = BuildMineTimerName(_lastInfestedMinePosition);
        var timer = new PlayTimer
        {
            Name = timerName,
            DurationSeconds = RoyalJellyRespawnSeconds,
            RemainingSeconds = RoyalJellyRespawnSeconds,
            IsRoyalJellyAuto = true,
            HasFinishedNotification = false,
            LocationKey = locationKey
        };

        _timers.Add(timer);

        StartTimer(timer);
        SaveTimers();
        Logger.LogMessage($"[PlaytimeTimers] Auto-started Royal Jelly 4h timer for {timerName}.");
    }

    private static string BuildMineLocationKey(string sceneName, Vector3 position)
    {
        var keyScene = string.IsNullOrWhiteSpace(sceneName) ? "scene" : sceneName.Trim().ToLowerInvariant();
        var snapX = Mathf.RoundToInt(position.x / MineCoordinateGridSize) * MineCoordinateGridSize;
        var snapZ = Mathf.RoundToInt(position.z / MineCoordinateGridSize) * MineCoordinateGridSize;
        return $"{keyScene}:{snapX:0}:{snapZ:0}";
    }

    private static string BuildMineTimerName(Vector3 position)
    {
        var x = Mathf.RoundToInt(position.x);
        var z = Mathf.RoundToInt(position.z);
        return $"{RoyalJellyTimerName} [{x}, {z}]";
    }

    private void DrawHudOverlay()
    {
        if (!_isInGameplayWorld)
        {
            return;
        }

        _hudVisibleTimers = _timers.ToList();
        if (_hudVisibleTimers.Count == 0)
        {
            return;
        }

        if (!_hudMovable.Value)
        {
            _hudRect.x = 20f;
            _hudRect.y = 20f;
        }

        ClampHudRectToScreen();

        var previousColor = GUI.color;
        var hudStyle = _showHudBackground.Value ? GUI.skin.window : GUIStyle.none;
        if (_showHudBackground.Value)
        {
            var alpha = Mathf.Clamp01(_hudBackgroundOpacity.Value);
            GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, alpha);
        }

        var previousPosition = new Vector2(_hudRect.x, _hudRect.y);
        _hudRect = GUILayout.Window(HudWindowId, _hudRect, DrawHudWindow, string.Empty, hudStyle);
        GUI.color = previousColor;

        if (Mathf.Abs(previousPosition.x - _hudRect.x) > 0.1f || Mathf.Abs(previousPosition.y - _hudRect.y) > 0.1f)
        {
            SaveHudPosition();
        }
    }

    private void DrawHudWindow(int id)
    {
        var previousColor = GUI.color;
        GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, 1f);

        GUILayout.Label("Playtime Timers");

        foreach (var timer in _hudVisibleTimers)
        {
            var clockText = timer.UseWorldClock ? "Server" : "Waiting";
            var status = timer.RemainingSeconds <= 0f ? "Finished" : (timer.IsRunning ? "Running" : "Paused");
            GUILayout.Label($"{timer.Name}: {FormatTime(timer.RemainingSeconds)} ({clockText}, {status})");
        }

        GUI.color = previousColor;
        if (_hudMovable.Value)
        {
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 10000f));
        }
    }

    private void SaveHudPosition()
    {
        ClampHudRectToScreen();
        _hudPositionX.Value = _hudRect.x;
        _hudPositionY.Value = _hudRect.y;
    }

    private void ClampHudRectToScreen()
    {
        var maxX = Mathf.Max(0f, Screen.width - _hudRect.width);
        var maxY = Mathf.Max(0f, Screen.height - _hudRect.height);
        _hudRect.x = Mathf.Clamp(_hudRect.x, 0f, maxX);
        _hudRect.y = Mathf.Clamp(_hudRect.y, 0f, maxY);
    }

    private static bool TryGetPlayerWorldPosition(out Vector3 position)
    {
        position = Vector3.zero;

        try
        {
            if (!TryGetLocalPlayer(out var localPlayer))
            {
                return false;
            }

            if (localPlayer is Component component)
            {
                position = component.transform.position;
                return true;
            }

            var transformProperty = localPlayer.GetType().GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (transformProperty?.GetValue(localPlayer) is Transform transform)
            {
                position = transform.position;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsPlayerInInterior()
    {
        try
        {
            if (!TryGetLocalPlayer(out var localPlayer))
            {
                return false;
            }

            var playerType = localPlayer.GetType();
            var inInteriorMethod = playerType.GetMethod("InInterior", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (inInteriorMethod == null)
            {
                return false;
            }

            var result = inInteriorMethod.Invoke(localPlayer, null);
            return result is bool inside && inside;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetWorldTimeSeconds(out double worldSeconds)
    {
        worldSeconds = 0d;

        try
        {
            var znetType = Type.GetType("ZNet, Assembly-CSharp");
            if (znetType == null)
            {
                return false;
            }

            var instanceProperty = znetType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var instanceField = znetType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var znetInstance = instanceProperty?.GetValue(null) ?? instanceField?.GetValue(null);
            if (znetInstance == null)
            {
                return false;
            }

            var getTimeSecondsMethod = znetType.GetMethod("GetTimeSeconds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getTimeSecondsMethod != null)
            {
                var value = getTimeSecondsMethod.Invoke(znetInstance, null);
                if (TryConvertToDouble(value, out worldSeconds))
                {
                    return true;
                }
            }

            var getTimeMethod = znetType.GetMethod("GetTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getTimeMethod != null)
            {
                var value = getTimeMethod.Invoke(znetInstance, null);
                if (TryConvertToDouble(value, out worldSeconds))
                {
                    return true;
                }
            }

            var netTimeField = znetType.GetField("m_netTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (netTimeField != null)
            {
                var value = netTimeField.GetValue(znetInstance);
                if (TryConvertToDouble(value, out worldSeconds))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasLocalPlayer()
    {
        return TryGetLocalPlayer(out _);
    }

    private static bool TryGetLocalPlayer(out object localPlayer)
    {
        localPlayer = null!;

        try
        {
            var playerType = Type.GetType("Player, Assembly-CSharp");
            if (playerType == null)
            {
                return false;
            }

            var localPlayerField = playerType.GetField("m_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            localPlayer = localPlayerField?.GetValue(null)!;
            if (localPlayer != null)
            {
                return true;
            }

            var localPlayerProperty = playerType.GetProperty("m_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                   ?? playerType.GetProperty("localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            localPlayer = localPlayerProperty?.GetValue(null)!;
            if (localPlayer != null)
            {
                return true;
            }

            var localPlayerMethod = playerType.GetMethod("GetLocalPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            localPlayer = localPlayerMethod?.Invoke(null, null)!;
            return localPlayer != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMainMenuScene()
    {
        try
        {
            var sceneName = SceneManager.GetActiveScene().name;
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return true;
            }

            var text = sceneName.Trim().ToLowerInvariant();
            return text == "start" || text.Contains("menu");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        result = 0d;
        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            default:
                return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
    }
}
