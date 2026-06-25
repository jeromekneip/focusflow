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

using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusFlow.Core;
using FocusFlow.Models;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using UIElement = System.Windows.UIElement;
using MouseButton = System.Windows.Input.MouseButton;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace FocusFlow.UI;

public partial class MiniTimerWindow : Window
{
    // Quiet Hours phase accents (sage focus / amber break / rose long / amber awaiting).
    private static readonly Brush FocusBrush =
        new SolidColorBrush(Color.FromRgb(0x7F, 0xB7, 0xA6)); // sage
    private static readonly Brush ShortBreakBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x8C)); // amber
    private static readonly Brush LongBreakBrush =
        new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0xA0)); // rose
    private static readonly Brush IdleBrush =
        new SolidColorBrush(Color.FromRgb(0xB7, 0xAE, 0xC2)); // muted
    private static readonly Brush AwaitingBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x8C)); // amber "waiting"
    private static readonly Brush PrimaryTextBrush =
        new SolidColorBrush(Color.FromRgb(0xF4, 0xEE, 0xE3)); // warm white

    private readonly Settings _settings;
    private bool _secondScale;

    public event Action? PauseClicked;
    public event Action? SkipClicked;
    public event Action? SettingsClicked;

    public MiniTimerWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        Topmost = settings.MiniTimerAlwaysOnTop;
        _secondScale = settings.IsSecondScale;

        Loaded += OnLoaded;
        LocationChanged += (_, _) => PersistPosition();
        Closing += (_, _) => PersistPosition();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Restore saved position, else dock near bottom-right of work area.
        if (!double.IsNaN(_settings.MiniTimerX) && !double.IsNaN(_settings.MiniTimerY)
            && IsOnScreen(_settings.MiniTimerX, _settings.MiniTimerY))
        {
            Left = _settings.MiniTimerX;
            Top = _settings.MiniTimerY;
        }
        else
        {
            // With SizeToContent="Height" the Height property is NaN until measured;
            // use the laid-out actual extents (valid by the Loaded event) so the
            // bottom-right docking math never produces NaN.
            double w = double.IsNaN(Width) ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - w - 24;
            Top = wa.Bottom - h - 24;
        }
    }

    private static bool IsOnScreen(double x, double y)
    {
        var va = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        return x >= left - 50 && y >= top - 50 &&
               x <= left + va - 50 && y <= top + vh - 50;
    }

    private void PersistPosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        _settings.MiniTimerX = Left;
        _settings.MiniTimerY = Top;
        _settings.Save();
    }

    public void ApplySettings(Settings settings)
    {
        Topmost = settings.MiniTimerAlwaysOnTop;
        _secondScale = settings.IsSecondScale;
    }

    public void SetSecondScale(bool secondScale) => _secondScale = secondScale;

    private string Format(TimeSpan t)
    {
        if (_secondScale && t.TotalMinutes < 1)
            return $"{(int)t.TotalSeconds:00}s";
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private static string TrackOut(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length * 2);
        string upper = s.ToUpperInvariant();
        for (int i = 0; i < upper.Length; i++)
        {
            sb.Append(upper[i]);
            if (i < upper.Length - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    public void UpdateTime(TimeSpan remaining)
    {
        TimeLabel.Text = Format(remaining);
    }

    public void UpdatePhase(Phase phase, bool isRunning)
    {
        // Any non-awaiting phase stops the gentle pulse.
        StopDotPulse();

        // The countdown numeral stays warm-white (the dominant mass); ONLY the
        // phase dot + small label carry the accent (dominant + spark). The
        // numeral only takes a tint in the non-numeric "Ready?" awaiting state.
        switch (phase)
        {
            case Phase.Idle:
                // Prose phrase (em-dash) -> NORMAL text, not tracked caps.
                PhaseLabel.Text = "Idle — press Start";
                PhaseLabel.Foreground = IdleBrush;
                PhaseDot.Fill = IdleBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.Focus:
                PhaseLabel.Text = TrackOut("Focus");
                PhaseLabel.Foreground = FocusBrush;
                PhaseDot.Fill = FocusBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.ShortBreak:
                PhaseLabel.Text = TrackOut("Short break");
                PhaseLabel.Foreground = ShortBreakBrush;
                PhaseDot.Fill = ShortBreakBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.LongBreak:
                PhaseLabel.Text = TrackOut("Long break");
                PhaseLabel.Foreground = LongBreakBrush;
                PhaseDot.Fill = LongBreakBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.AwaitingReturn:
                PhaseLabel.Text = TrackOut("Break over");
                PhaseLabel.Foreground = AwaitingBrush;
                PhaseDot.Fill = AwaitingBrush;
                // "Ready?" is a status word, not a countdown — accent reads here.
                TimeLabel.Foreground = AwaitingBrush;
                TimeLabel.Text = "Ready?";
                StartDotPulse();
                break;
        }

        UpdateButtonLabel(phase, isRunning);
    }

    private void StartDotPulse()
    {
        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(1.1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        PhaseDot.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    private void StopDotPulse()
    {
        PhaseDot.BeginAnimation(UIElement.OpacityProperty, null);
        PhaseDot.Opacity = 1.0;
    }

    public void UpdateButtonLabel(Phase phase, bool isRunning)
    {
        // Show the state-appropriate control(s); everything is visible at rest.
        switch (phase)
        {
            case Phase.Idle:
                // Only the primary Start pill.
                StartButton.Visibility = Visibility.Visible;
                RunControls.Visibility = Visibility.Collapsed;
                break;

            case Phase.AwaitingReturn:
                // The primary action becomes the confirm affordance. No Skip here
                // — Skip would just bypass the gate; the "I'm back" button is the
                // single clear action (and the engine treats it as a confirm).
                StartButton.Visibility = Visibility.Collapsed;
                RunControls.Visibility = Visibility.Visible;
                PauseButton.Content = "I'm back";
                SkipButton.Visibility = Visibility.Collapsed;
                break;

            default:
                // Running phases (Focus / ShortBreak / LongBreak): Pause/Resume + Skip.
                StartButton.Visibility = Visibility.Collapsed;
                RunControls.Visibility = Visibility.Visible;
                SkipButton.Visibility = Visibility.Visible;
                PauseButton.Content = isRunning ? "Pause" : "Resume";
                break;
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* DragMove can throw if not pressed */ }
        }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => PauseClicked?.Invoke();

    private void OnSkipClick(object sender, RoutedEventArgs e) => SkipClicked?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsClicked?.Invoke();
}
