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

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FocusFlow.Models;
using MouseButton = System.Windows.Input.MouseButton;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace FocusFlow.UI;

/// <summary>
/// Edits a working copy of Settings. On Save, raises SettingsSaved with the new
/// instance; the App applies durations to the engine and the autostart registry.
/// </summary>
public partial class SettingsWindow : Window
{
    private Settings _working;

    // Inline validation: warning border tint applied to invalid fields.
    private static readonly SolidColorBrush WarningBorder =
        new(System.Windows.Media.Color.FromArgb(0xCC, 0xE8, 0x7A, 0x4A));

    /// <summary>Raised with the saved settings instance when the user clicks Save.</summary>
    public event Action<Settings>? SettingsSaved;

    public SettingsWindow(Settings current)
    {
        InitializeComponent();
        // Work on a clone so Cancel discards edits.
        _working = Clone(current);
        LoadIntoUi();

        // Wire up inline validation handlers for the four numeric inputs.
        WireValidation(FocusBox);
        WireValidation(ShortBox);
        WireValidation(LongBox);
        WireValidation(BlocksBox);

        // Wire reflection placeholder visibility (L5).
        ReflectionBox.TextChanged += (_, _) => UpdateReflectionPlaceholder();
        UpdateReflectionPlaceholder();

        // Entrance fade-in (L3) — mirrors the overlay RootLayer entrance.
        Loaded += (_, _) => BeginEntranceFade();
    }

    /// <summary>Attach digit-only input filtering and live validation to a TextBox.</summary>
    private void WireValidation(TextBox box)
    {
        // Block non-digit keypresses at input time.
        box.PreviewTextInput += OnNumericPreviewTextInput;
        // Block paste of non-numeric content.
        System.Windows.DataObject.AddPastingHandler(box, OnNumericPaste);
        // Re-validate (update Save button state) on every text change.
        box.TextChanged += (_, _) => UpdateSaveButtonState();
    }

    /// <summary>Allow only digit characters through PreviewTextInput.</summary>
    private void OnNumericPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Reject anything that is not a digit string.
        e.Handled = !IsAllDigits(e.Text);
    }

    /// <summary>Strip non-digit characters from pasted text.</summary>
    private void OnNumericPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string pasted = (string)e.DataObject.GetData(typeof(string));
            if (!IsAllDigits(pasted))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    private static bool IsAllDigits(string? s) =>
        !string.IsNullOrEmpty(s) && s.All(char.IsDigit);

    /// <summary>
    /// Apply or remove the inline warning state on a TextBox. Uses BorderBrush +
    /// a warning tint to signal invalid input without a MessageBox.
    /// </summary>
    private static void SetFieldError(TextBox box, bool hasError)
    {
        if (hasError)
        {
            box.BorderBrush = WarningBorder;
            box.BorderThickness = new Thickness(1.5);
        }
        else
        {
            // Reset to the FieldBox template defaults (hairline-hi, 1px).
            box.ClearValue(TextBox.BorderBrushProperty);
            box.ClearValue(TextBox.BorderThicknessProperty);
        }
    }

    /// <summary>
    /// Validate all four numeric fields; update each field's error state and
    /// enable/disable the Save button accordingly.
    /// </summary>
    private void UpdateSaveButtonState()
    {
        bool focusOk = TryParsePositive(FocusBox.Text, out _);
        bool shortOk = TryParsePositive(ShortBox.Text, out _);
        bool longOk = TryParsePositive(LongBox.Text, out _);
        bool blocksOk = TryParsePositive(BlocksBox.Text, out _);

        SetFieldError(FocusBox, !focusOk);
        SetFieldError(ShortBox, !shortOk);
        SetFieldError(LongBox, !longOk);
        SetFieldError(BlocksBox, !blocksOk);

        SaveButton.IsEnabled = focusOk && shortOk && longOk && blocksOk;
    }

    /// <summary>Brief entrance fade-in to match the overlay's RootLayer stagger (L3).</summary>
    private void BeginEntranceFade()
    {
        Opacity = 0;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
            TimeSpan.FromSeconds(0.28))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        BeginAnimation(OpacityProperty, fade);
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
        ReduceMotion = s.ReduceMotion,
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
        ReduceMotionCheck.IsChecked = _working.ReduceMotion;
        ReflectionBox.Text = _working.ReflectionPrompt;

        // Sync Save button state after populating (all valid on initial load).
        UpdateSaveButtonState();
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

        // Inline validation already guards Save button; re-check defensively.
        if (!TryParsePositive(FocusBox.Text, out int focus) ||
            !TryParsePositive(ShortBox.Text, out int shortB) ||
            !TryParsePositive(LongBox.Text, out int longB) ||
            !TryParsePositive(BlocksBox.Text, out int blocks))
        {
            // Should not reach here with the button gating, but keep as a safe-stop.
            UpdateSaveButtonState();
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
        _working.ReduceMotion = ReduceMotionCheck.IsChecked == true;
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

    /// <summary>
    /// Show the placeholder watermark when the reflection box is empty;
    /// hide it as soon as the user types anything.
    /// </summary>
    private void UpdateReflectionPlaceholder()
    {
        ReflectionPlaceholder.Visibility =
            string.IsNullOrEmpty(ReflectionBox.Text) ? Visibility.Visible : Visibility.Collapsed;
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
