using System.Globalization;
using System.Windows;
using FocusFlow.Models;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MouseButton = System.Windows.Input.MouseButton;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace FocusFlow.UI;

/// <summary>
/// Edits a working copy of Settings. On Save, raises SettingsSaved with the new
/// instance; the App applies durations to the engine and the autostart registry.
/// </summary>
public partial class SettingsWindow : Window
{
    private Settings _working;

    /// <summary>Raised with the saved settings instance when the user clicks Save.</summary>
    public event Action<Settings>? SettingsSaved;

    public SettingsWindow(Settings current)
    {
        InitializeComponent();
        // Work on a clone so Cancel discards edits.
        _working = Clone(current);
        LoadIntoUi();
    }

    private static Settings Clone(Settings s) => new()
    {
        FocusSeconds = s.FocusSeconds,
        ShortBreakSeconds = s.ShortBreakSeconds,
        LongBreakSeconds = s.LongBreakSeconds,
        BlocksPerLongBreak = s.BlocksPerLongBreak,
        SoundEnabled = s.SoundEnabled,
        AutoStartWithWindows = s.AutoStartWithWindows,
        MiniTimerAlwaysOnTop = s.MiniTimerAlwaysOnTop,
        OverlayEnabled = s.OverlayEnabled,
        ConfirmReturnAfterBreak = s.ConfirmReturnAfterBreak,
        ReflectionPrompt = s.ReflectionPrompt,
        MiniTimerX = s.MiniTimerX,
        MiniTimerY = s.MiniTimerY,
        ActivePreset = s.ActivePreset
    };

    private void LoadIntoUi()
    {
        bool secondScale = _working.IsSecondScale;
        SecondScaleNote.Visibility = secondScale ? Visibility.Visible : Visibility.Collapsed;

        if (secondScale)
        {
            // Show raw seconds for the Test preset.
            FocusBox.Text = _working.FocusSeconds.ToString(CultureInfo.InvariantCulture);
            ShortBox.Text = _working.ShortBreakSeconds.ToString(CultureInfo.InvariantCulture);
            LongBox.Text = _working.LongBreakSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            FocusBox.Text = _working.FocusMinutes.ToString(CultureInfo.InvariantCulture);
            ShortBox.Text = _working.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
            LongBox.Text = _working.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
        }

        BlocksBox.Text = _working.BlocksPerLongBreak.ToString(CultureInfo.InvariantCulture);
        SoundCheck.IsChecked = _working.SoundEnabled;
        AutoStartCheck.IsChecked = _working.AutoStartWithWindows;
        OnTopCheck.IsChecked = _working.MiniTimerAlwaysOnTop;
        OverlayCheck.IsChecked = _working.OverlayEnabled;
        ConfirmReturnCheck.IsChecked = _working.ConfirmReturnAfterBreak;
        ReflectionBox.Text = _working.ReflectionPrompt;

        HighlightActivePreset();
    }

    /// <summary>
    /// Mark the active preset's segment chip as selected (accent fill/outline) so
    /// the current rhythm is visible. Purely presentational — Tag drives the
    /// SegmentButton's "Selected" visual trigger; it touches no engine state.
    /// </summary>
    private void HighlightActivePreset()
    {
        PomodoroButton.Tag = null;
        DeepWorkButton.Tag = null;
        UltradianButton.Tag = null;
        TestButton.Tag = null;

        var active = _working.ActivePreset switch
        {
            Preset.Pomodoro => PomodoroButton,
            Preset.DeepWork => DeepWorkButton,
            Preset.Ultradian => UltradianButton,
            Preset.Test => TestButton,
            _ => DeepWorkButton
        };
        active.Tag = "Selected";
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        Preset preset = sender switch
        {
            var b when ReferenceEquals(b, PomodoroButton) => Preset.Pomodoro,
            var b when ReferenceEquals(b, DeepWorkButton) => Preset.DeepWork,
            var b when ReferenceEquals(b, UltradianButton) => Preset.Ultradian,
            var b when ReferenceEquals(b, TestButton) => Preset.Test,
            _ => Preset.DeepWork
        };
        _working.ApplyPreset(preset);
        LoadIntoUi();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        bool secondScale = _working.IsSecondScale;

        if (!TryParsePositive(FocusBox.Text, out int focus) ||
            !TryParsePositive(ShortBox.Text, out int shortB) ||
            !TryParsePositive(LongBox.Text, out int longB) ||
            !TryParsePositive(BlocksBox.Text, out int blocks))
        {
            MessageBox.Show(this,
                "Durations and blocks must be positive whole numbers.",
                "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (secondScale)
        {
            _working.FocusSeconds = focus;
            _working.ShortBreakSeconds = shortB;
            _working.LongBreakSeconds = longB;
        }
        else
        {
            _working.FocusMinutes = focus;
            _working.ShortBreakMinutes = shortB;
            _working.LongBreakMinutes = longB;
        }

        _working.BlocksPerLongBreak = blocks;
        _working.SoundEnabled = SoundCheck.IsChecked == true;
        _working.AutoStartWithWindows = AutoStartCheck.IsChecked == true;
        _working.MiniTimerAlwaysOnTop = OnTopCheck.IsChecked == true;
        _working.OverlayEnabled = OverlayCheck.IsChecked == true;
        _working.ConfirmReturnAfterBreak = ConfirmReturnCheck.IsChecked == true;
        _working.ReflectionPrompt = ReflectionBox.Text;

        _working.Save();
        SettingsSaved?.Invoke(_working);
        Close();
    }

    private static bool TryParsePositive(string text, out int value)
    {
        return int.TryParse(text?.Trim(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Drag the borderless window by its header. Mirrors MiniTimerWindow.OnDrag:
    /// guard on the left button and swallow the rare DragMove exception.
    /// </summary>
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* DragMove can throw if not pressed */ }
        }
    }
}
