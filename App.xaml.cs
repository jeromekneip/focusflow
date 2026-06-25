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

using System.Collections.Generic;
using System.IO;
using FocusFlow.Core;
using FocusFlow.Models;
using FocusFlow.Services;
using FocusFlow.UI;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;
using Window = System.Windows.Window;
using ImageSource = System.Windows.Media.ImageSource;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using BitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;
using BitmapCreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions;

namespace FocusFlow;

/// <summary>
/// Application entry + wiring hub. Single-instance via named Mutex. Owns the
/// engine, services, mini timer, and per-monitor break overlays, and routes
/// engine events to the UI/notifier and UI/tray actions back to the engine.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "FocusFlow_SingleInstance_Mutex_{8E0F7A12}";
    private Mutex? _instanceMutex;

    private Settings _settings = null!;
    private TimerEngine _engine = null!;
    private TrayIconService _tray = null!;
    private Notifier _notifier = null!;
    private MiniTimerWindow _miniTimer = null!;
    private readonly List<BreakOverlayWindow> _overlays = new();

    private bool _miniTimerVisible = true;

    // Auto-hide bookkeeping for the break overlay. When real overlays are shown
    // we hide the always-on-top mini timer (it otherwise peeks through the full
    // screen overlay), remembering its prior visibility so we can restore EXACTLY
    // that when the overlay closes — honoring the user's manual tray Show/Hide.
    private bool _miniTimerHiddenForOverlay = false;
    private bool _miniTimerVisibleBeforeOverlay = true;

    /// <summary>
    /// Stable taskbar/toast identity. Set BEFORE any window exists so Windows
    /// associates the running process with the pinned shortcut (prevents the
    /// duplicate pinned-vs-running taskbar button) and routes toasts correctly.
    /// </summary>
    private const string AppUserModelId = "SLG.FocusFlow";

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);

    // Window-icon source (alt-tab / taskbar representation). Loaded once from the
    // same Assets\app.ico the tray uses; null if missing so we degrade gracefully.
    private ImageSource? _windowIcon;

    private ImageSource? LoadWindowIcon()
    {
        if (_windowIcon != null) return _windowIcon;
        try
        {
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                var uri = new Uri(icoPath, UriKind.Absolute);
                _windowIcon = BitmapFrame.Create(
                    uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
        catch { /* leave null: window keeps the default icon */ }
        return _windowIcon;
    }

    /// <summary>Apply the app icon to a window's title-bar/alt-tab representation.</summary>
    private void ApplyIcon(Window window)
    {
        var icon = LoadWindowIcon();
        if (icon != null) window.Icon = icon;
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // ---- Explicit AppUserModelID (must precede ANY window creation) ----
        // Wrapped so a failure here never blocks startup.
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* non-fatal: taskbar identity falls back to default */ }

        // ---- Single-instance guard ----
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the mutex; quit silently.
            Shutdown();
            return;
        }

        _settings = Settings.Load();

        // First-run autostart reconciliation (default is false -> harmless).
        AutostartService.Apply(_settings.AutoStartWithWindows);

        _engine = new TimerEngine(_settings);

        _tray = new TrayIconService();
        _notifier = new Notifier(_tray, _settings);
        _miniTimer = new MiniTimerWindow(_settings);
        ApplyIcon(_miniTimer);

        WireEngineEvents();
        WireTrayEvents();
        WireMiniTimerEvents();

        // Engine starts in Idle (no auto-counting); show mini timer by default.
        _miniTimer.UpdatePhase(Phase.Idle, false);
        _miniTimer.UpdateTime(TimeSpan.Zero);
        _miniTimer.Show();
        _miniTimerVisible = true;
        _tray.SetMiniTimerVisible(true);
        _tray.SetRunningState(false, Phase.Idle);
        UpdateTooltip(Phase.Idle, TimeSpan.Zero);

        // Discoverable hint that the user should press Start.
        _tray.ShowBalloon("FocusFlow is running",
            "Right-click the tray icon (or use the mini timer) to Start your first focus block.");
    }

    // ---------------- Engine -> UI/Notifier ----------------

    private void WireEngineEvents()
    {
        _engine.Tick += remaining =>
        {
            _miniTimer.UpdateTime(remaining);
            foreach (var o in _overlays) o.UpdateTime(remaining);
            UpdateTooltip(_engine.CurrentPhase, remaining);
        };

        _engine.PhaseChanged += phase =>
        {
            _miniTimer.UpdatePhase(phase, _engine.IsRunning);
            _tray.SetRunningState(_engine.IsRunning, phase);
            _notifier.NotifyPhase(phase);
            UpdateTooltip(phase, _engine.Remaining);

            bool isBreak = phase is Phase.ShortBreak or Phase.LongBreak;
            if (isBreak && _settings.OverlayEnabled)
            {
                ShowOverlays(phase);
            }
            else if (phase == Phase.AwaitingReturn)
            {
                // Break's over but unconfirmed: keep any open overlays and flip
                // them to the confirm state. If overlays are off there are none
                // to keep — the mini timer + toast carry the confirm affordance.
                SwitchOverlaysToConfirm();
            }
            else
            {
                CloseOverlays();
            }
        };

        _engine.CycleCompleted += () =>
        {
            // A full cycle (long break finished). Toast already fired via phase change.
        };
    }

    private void UpdateTooltip(Phase phase, TimeSpan remaining)
    {
        string time = phase switch
        {
            Phase.Idle => "Idle",
            Phase.AwaitingReturn => "Paused: click 'I'm back' to resume",
            _ => $"{phase} {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}"
        };
        _tray.SetTooltip($"FocusFlow: {time}");
    }

    // ---------------- Tray menu -> Engine ----------------

    private void WireTrayEvents()
    {
        _tray.StartPauseClicked += () =>
        {
            _engine.TogglePause();
            RefreshRunStateUi();
        };

        _tray.SkipClicked += () =>
        {
            _engine.Skip();
            RefreshRunStateUi();
        };

        _tray.ToggleMiniTimerClicked += ToggleMiniTimer;

        _tray.ConfirmReturnClicked += () =>
        {
            _engine.ConfirmReturn();
            RefreshRunStateUi();
        };

        _tray.SettingsClicked += OpenSettings;

        _tray.ExitClicked += () => Shutdown();
    }

    private void WireMiniTimerEvents()
    {
        _miniTimer.PauseClicked += () =>
        {
            _engine.TogglePause();
            RefreshRunStateUi();
        };

        _miniTimer.SkipClicked += () =>
        {
            _engine.Skip();
            RefreshRunStateUi();
        };

        // Gear icon on the mini timer reuses the SAME OpenSettings() method
        // wired to the tray "Settings…" menu item — single source of truth.
        _miniTimer.SettingsClicked += OpenSettings;
    }

    private void RefreshRunStateUi()
    {
        _tray.SetRunningState(_engine.IsRunning, _engine.CurrentPhase);
        _miniTimer.UpdateButtonLabel(_engine.CurrentPhase, _engine.IsRunning);
        _miniTimer.UpdatePhase(_engine.CurrentPhase, _engine.IsRunning);
    }

    private void ToggleMiniTimer()
    {
        if (_miniTimerVisible)
        {
            _miniTimer.Hide();
            _miniTimerVisible = false;
        }
        else
        {
            _miniTimer.Show();
            _miniTimer.Activate();
            _miniTimerVisible = true;
        }
        _tray.SetMiniTimerVisible(_miniTimerVisible);
    }

    // ---------------- Break overlays ----------------

    private void ShowOverlays(Phase phase)
    {
        // Tear down any existing overlay windows WITHOUT running the mini-timer
        // restore — we're replacing them, not returning to focus. The restore is
        // owned solely by CloseOverlays (the genuine "overlay gone" path).
        DisposeOverlayWindows();

        // A genuine overlay is about to cover the screen (this method is only
        // reached when Settings.OverlayEnabled == true). Hide the mini timer so
        // it doesn't peek through, remembering whatever visibility it had — manual
        // tray toggle included — so CloseOverlays restores exactly that. Guard
        // against double-capture if ShowOverlays runs twice without a close.
        if (!_miniTimerHiddenForOverlay)
        {
            _miniTimerVisibleBeforeOverlay = _miniTimerVisible;
            _miniTimerHiddenForOverlay = true;
            if (_miniTimerVisible)
            {
                _miniTimer.Hide();
                _miniTimerVisible = false;
            }
        }

        string title = phase == Phase.LongBreak ? "Long break" : "Short break";
        bool secondScale = _settings.IsSecondScale;

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var overlay = new BreakOverlayWindow(screen, _settings.ReflectionPrompt, secondScale, phase, _settings.ReduceMotion);
            ApplyIcon(overlay);
            overlay.SetPhaseTitle(title);
            overlay.UpdateTime(_engine.Remaining);

            overlay.BackClicked += () =>
            {
                _engine.Skip(); // break -> Focus; PhaseChanged closes overlays
                RefreshRunStateUi();
            };
            overlay.ExtendClicked += () =>
            {
                _engine.AddFiveMinutes();
            };
            overlay.ConfirmClicked += () =>
            {
                _engine.ConfirmReturn(); // AwaitingReturn -> Focus; PhaseChanged closes overlays
                RefreshRunStateUi();
            };
            overlay.PostponeClicked += () =>
            {
                _engine.PostponeBreak(); // break -> transient Focus; PhaseChanged closes overlays
                // Show the soft-cap nudge on every overlay once the threshold is reached.
                if (_engine.PostponeCount >= TimerEngine.PostponeNudgeThreshold)
                {
                    foreach (var o in _overlays) o.ShowPostponeNudge();
                }
                RefreshRunStateUi();
            };

            _overlays.Add(overlay);
            overlay.Show();
        }
    }

    /// <summary>
    /// Flip any open break overlays into the "welcome back / confirm" state.
    /// Called when the engine enters AwaitingReturn. Overlays are NOT closed here
    /// — only ConfirmReturn (or Skip) closes them, via the next PhaseChanged.
    /// </summary>
    private void SwitchOverlaysToConfirm()
    {
        DateTime since = _engine.AwaitingSince ?? DateTime.Now;
        foreach (var o in _overlays)
        {
            o.EnterConfirmState(
                "Break's over",
                "Take a breath. When you're back at your desk and ready, confirm to start the next focus block.",
                since);
        }
    }

    /// <summary>Close + clear the overlay windows only (no mini-timer restore).</summary>
    private void DisposeOverlayWindows()
    {
        foreach (var o in _overlays)
        {
            try { o.Close(); } catch { /* ignore */ }
        }
        _overlays.Clear();
    }

    private void CloseOverlays()
    {
        DisposeOverlayWindows();

        // Restore the mini timer to whatever visibility it had before the overlay
        // hid it — and only if WE hid it. If the user had it manually hidden, this
        // leaves it hidden; if shown, it comes back. The tray checkmark is synced
        // either way so it never drifts from the actual window state.
        if (_miniTimerHiddenForOverlay)
        {
            _miniTimerHiddenForOverlay = false;
            if (_miniTimerVisibleBeforeOverlay && !_miniTimerVisible)
            {
                _miniTimer.Show();
                _miniTimerVisible = true;
            }
            _tray.SetMiniTimerVisible(_miniTimerVisible);
        }
    }

    // ---------------- Settings ----------------

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings);
        ApplyIcon(win);
        win.SettingsSaved += ApplyNewSettings;
        win.ShowDialog();
    }

    private void ApplyNewSettings(Settings updated)
    {
        bool autoStartChanged = updated.AutoStartWithWindows != _settings.AutoStartWithWindows;

        _settings = updated;
        _engine.UpdateSettings(_settings);
        _notifier.UpdateSettings(_settings);
        _miniTimer.ApplySettings(_settings);

        if (autoStartChanged)
        {
            AutostartService.Apply(_settings.AutoStartWithWindows);
        }
    }

    // ---------------- Shutdown ----------------

    private void OnExit(object sender, ExitEventArgs e)
    {
        try { _settings?.Save(); } catch { /* ignore */ }
        CloseOverlays();
        _tray?.Dispose();
        _instanceMutex?.Dispose();
    }
}
