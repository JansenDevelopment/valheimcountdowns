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
    public const string PluginVersion = "1.2.0";

    private const float SaveIntervalSeconds = 10f;

    private readonly List<PlayTimer> _timers = new();

    private ConfigEntry<KeyboardShortcut> _toggleWindowKey = null!;
    private ConfigEntry<string> _serializedTimers = null!;
    private ConfigEntry<bool> _pauseWhenGamePaused = null!;
    private ConfigEntry<bool> _autoStartRoyalJellyOnMineExit = null!;
    private ConfigEntry<bool> _showHudWhenMenuClosed = null!;

    private Rect _windowRect = new(200f, 120f, 560f, 480f);
    private Vector2 _scrollPosition;
    private bool _showWindow;
    private float _saveAccumulator;

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
        _pauseWhenGamePaused = Config.Bind("General", "Pause When Game Paused", true, "If true, countdown pauses when Time.timeScale <= 0.");
        _autoStartRoyalJellyOnMineExit = Config.Bind("General", "Auto Start Royal Jelly On Mine Exit", true, "Automatically adds/starts a 4h Royal Jelly timer for an Infested Mine location when you leave it (no duplicate location timers).");
        _showHudWhenMenuClosed = Config.Bind("General", "Show HUD When Menu Closed", true, "Show running timers on HUD while the F8 timer menu is closed.");
        _serializedTimers = Config.Bind("Storage", "Timers", string.Empty, "Internal timer storage. Do not edit manually.");

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

        HandleAutoStartRoyalJellyTimer();

        var delta = GetCountdownDelta();
        var hasWorldTime = TryGetWorldTimeSeconds(out var worldSeconds);
        if (delta > 0f || hasWorldTime)
        {
            foreach (var timer in _timers)
            {
                if (!timer.IsRunning || timer.RemainingSeconds <= 0f)
                {
                    continue;
                }

                if (timer.UseWorldClock && hasWorldTime)
                {
                    var elapsed = worldSeconds - timer.WorldStartSeconds;
                    if (elapsed < 0d)
                    {
                        timer.WorldStartSeconds = worldSeconds;
                        elapsed = 0d;
                    }

                    timer.RemainingSeconds = Mathf.Max(0f, timer.DurationSeconds - (float)elapsed);
                }
                else if (delta > 0f)
                {
                    timer.RemainingSeconds = Mathf.Max(0f, timer.RemainingSeconds - delta);
                }

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

    private float GetCountdownDelta()
    {
        if (_pauseWhenGamePaused.Value && Time.timeScale <= 0.001f)
        {
            return 0f;
        }

        return Time.unscaledDeltaTime;
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

        GUILayout.Label("Countdown timers use server/world time when available. Sleep won't speed them up.");

        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(260));
        foreach (var timer in _timers.ToList())
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label($"{timer.Name}");
            GUILayout.Label($"Remaining: {FormatTime(timer.RemainingSeconds)} / {FormatTime(timer.DurationSeconds)}");
            GUILayout.Label(timer.UseWorldClock ? "Clock: Server/World" : "Clock: Local Real-Time");

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
            }

            if (GUILayout.Button("+1m", GUILayout.Width(80)))
            {
                timer.DurationSeconds += 60f;
                timer.RemainingSeconds += 60f;
                if (timer.IsRunning && timer.UseWorldClock && TryGetWorldTimeSeconds(out var worldNowPlus))
                {
                    timer.WorldStartSeconds = worldNowPlus - (timer.DurationSeconds - timer.RemainingSeconds);
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

            _timers.Add(new PlayTimer
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Timer" : name,
                DurationSeconds = Mathf.Max(1f, duration),
                RemainingSeconds = Mathf.Clamp(remaining, 0f, Mathf.Max(1f, duration)),
                IsRunning = running && remaining > 0f,
                HasFinishedNotification = remaining <= 0f,
                UseWorldClock = useWorldClock,
                WorldStartSeconds = worldStartSeconds,
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
            EncodeName(timer.LocationKey)
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
        public bool IsRoyalJellyAuto;
        public string LocationKey = string.Empty;
    }

    private void StartTimer(PlayTimer timer)
    {
        timer.IsRunning = true;
        timer.HasFinishedNotification = false;

        if (TryGetWorldTimeSeconds(out var worldNow))
        {
            timer.UseWorldClock = true;
            timer.WorldStartSeconds = worldNow - (timer.DurationSeconds - timer.RemainingSeconds);
        }
        else
        {
            timer.UseWorldClock = false;
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
        if (inInterior && LooksLikeInfestedMineScene(sceneName))
        {
            _insideInfestedMine = true;
            _lastInfestedMineScene = sceneName;
            if (TryGetPlayerWorldPosition(out var playerPosition))
            {
                _lastInfestedMinePosition = playerPosition;
                _hasLastInfestedMinePosition = true;
            }
        }

        if (!inInterior && _lastInInterior && _insideInfestedMine)
        {
            AddAutoRoyalJellyTimerForLocation();
            _insideInfestedMine = false;
            _lastInfestedMineScene = string.Empty;
            _hasLastInfestedMinePosition = false;
        }

        if (!inInterior)
        {
            _insideInfestedMine = false;
        }

        _lastInInterior = inInterior;
    }

    private static bool LooksLikeInfestedMineScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return false;
        }

        var text = sceneName.ToLowerInvariant();
        return text.Contains("infested") || text.Contains("mine") || text.Contains("dvergr");
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
        var visibleTimers = _timers.Where(t => t.IsRunning && t.RemainingSeconds > 0f).ToList();
        if (visibleTimers.Count == 0)
        {
            return;
        }

        GUILayout.BeginArea(new Rect(20f, 20f, 440f, 300f), GUI.skin.box);
        GUILayout.Label("Playtime Timers");

        foreach (var timer in visibleTimers)
        {
            var clockText = timer.UseWorldClock ? "Server" : "Local";
            var status = timer.IsRunning ? "Running" : "Paused";
            GUILayout.Label($"{timer.Name}: {FormatTime(timer.RemainingSeconds)} ({clockText}, {status})");
        }

        GUILayout.EndArea();
    }

    private static bool TryGetPlayerWorldPosition(out Vector3 position)
    {
        position = Vector3.zero;

        try
        {
            var playerType = Type.GetType("Player, Assembly-CSharp");
            if (playerType == null)
            {
                return false;
            }

            var localPlayerField = playerType.GetField("m_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var localPlayer = localPlayerField?.GetValue(null);
            if (localPlayer == null)
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
            var playerType = Type.GetType("Player, Assembly-CSharp");
            if (playerType == null)
            {
                return false;
            }

            var localPlayerField = playerType.GetField("m_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var localPlayer = localPlayerField?.GetValue(null);
            if (localPlayer == null)
            {
                return false;
            }

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
