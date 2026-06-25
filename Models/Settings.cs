// FocusFlow — a Pomodoro-style focus timer for Windows.
// Copyright © 2026 Jerome Kneip
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusFlow.Models;

/// <summary>
/// Named timer presets. Test is QA-only with second-scale durations.
/// </summary>
public enum Preset
{
    Pomodoro,
    DeepWork,
    Ultradian,
    Test
}

/// <summary>
/// Persisted user settings. Durations are stored INTERNALLY IN SECONDS so the
/// Test preset can use tiny values; the regular presets store minutes * 60.
/// </summary>
public sealed class Settings
{
    // Durations stored in SECONDS internally.
    public int FocusSeconds { get; set; } = 50 * 60;
    public int ShortBreakSeconds { get; set; } = 10 * 60;
    public int LongBreakSeconds { get; set; } = 30 * 60;
    public int BlocksPerLongBreak { get; set; } = 3;

    public bool SoundEnabled { get; set; } = true;

    // Default false to avoid surprising the user (documented choice).
    public bool AutoStartWithWindows { get; set; } = false;

    public bool MiniTimerAlwaysOnTop { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;

    // When true, a finished break pauses in AwaitingReturn until the user
    // confirms they are back at their desk before the next focus block starts.
    // Default true so the at-the-desk confirmation is on out of the box.
    public bool ConfirmReturnAfterBreak { get; set; } = true;

    public string ReflectionPrompt { get; set; } =
        "Step back: are you still working on the right thing? What's the bigger picture?";

    // Saved mini-timer window position. NaN means "not set yet".
    public double MiniTimerX { get; set; } = double.NaN;
    public double MiniTimerY { get; set; } = double.NaN;

    // Which preset is active (informational; durations above are authoritative).
    public Preset ActivePreset { get; set; } = Preset.DeepWork;

    /// <summary>
    /// When true, continuously-looping animations (ring breathing, dot pulse)
    /// are suppressed. The app also gates on SystemParameters.ClientAreaAnimation
    /// at runtime so OS-level "Reduce motion" is always honoured regardless of this
    /// flag. Default false (animations on).
    /// </summary>
    public bool ReduceMotion { get; set; } = false;

    // ---- Convenience minute accessors for the UI (whole minutes) ----
    [JsonIgnore]
    public int FocusMinutes
    {
        get => FocusSeconds / 60;
        set => FocusSeconds = value * 60;
    }

    [JsonIgnore]
    public int ShortBreakMinutes
    {
        get => ShortBreakSeconds / 60;
        set => ShortBreakSeconds = value * 60;
    }

    [JsonIgnore]
    public int LongBreakMinutes
    {
        get => LongBreakSeconds / 60;
        set => LongBreakSeconds = value * 60;
    }

    /// <summary>True when durations are sub-minute (Test preset) so UI shows seconds.</summary>
    [JsonIgnore]
    public bool IsSecondScale =>
        FocusSeconds < 60 || ShortBreakSeconds < 60 || LongBreakSeconds < 60;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string AppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FocusFlow");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    /// <summary>Apply a named preset's durations to this Settings instance.</summary>
    public void ApplyPreset(Preset preset)
    {
        ActivePreset = preset;
        switch (preset)
        {
            case Preset.Pomodoro:
                FocusSeconds = 25 * 60;
                ShortBreakSeconds = 5 * 60;
                LongBreakSeconds = 15 * 60;
                BlocksPerLongBreak = 4;
                break;
            case Preset.DeepWork:
                FocusSeconds = 50 * 60;
                ShortBreakSeconds = 10 * 60;
                LongBreakSeconds = 30 * 60;
                BlocksPerLongBreak = 3;
                break;
            case Preset.Ultradian:
                FocusSeconds = 90 * 60;
                ShortBreakSeconds = 20 * 60;
                LongBreakSeconds = 20 * 60;
                BlocksPerLongBreak = 2;
                break;
            case Preset.Test:
                // QA-only: seconds, so a full cycle runs in under a minute.
                FocusSeconds = 5;
                ShortBreakSeconds = 3;
                LongBreakSeconds = 4;
                BlocksPerLongBreak = 2;
                break;
        }
    }

    /// <summary>
    /// Load settings from %APPDATA%\FocusFlow\settings.json. If missing or
    /// unparseable, return defaults (Deep Work) and persist them.
    /// </summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                Settings? loaded = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt / unreadable -> fall through to defaults.
        }

        var defaults = new Settings();
        defaults.ApplyPreset(Preset.DeepWork);
        defaults.Save();
        return defaults;
    }

    /// <summary>Clamp obviously-invalid values so a hand-edited file can't break the engine.</summary>
    private void Sanitize()
    {
        if (FocusSeconds < 1) FocusSeconds = 1;
        if (ShortBreakSeconds < 1) ShortBreakSeconds = 1;
        if (LongBreakSeconds < 1) LongBreakSeconds = 1;
        if (BlocksPerLongBreak < 1) BlocksPerLongBreak = 1;
    }

    /// <summary>Create the dir if needed and write indented JSON.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence; never crash the app over a failed save.
        }
    }
}
