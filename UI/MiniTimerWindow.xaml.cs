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
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
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
    // L1: IdleBrush resolved from the theme token at load time instead of a
    // hard-coded #B7AEC2. Theme.xaml lifts muted to #C6BFD2; this stays in sync.
    private Brush _idleBrush = System.Windows.SystemColors.GrayTextBrush; // replaced in OnLoaded
    private static readonly Brush AwaitingBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x8C)); // amber "waiting"
    private static readonly Brush PrimaryTextBrush =
        new SolidColorBrush(Color.FromRgb(0xF4, 0xEE, 0xE3)); // warm white

    private readonly Settings _settings;
    private bool _secondScale;

    // H1: last-rendered cycle dot state to avoid redundant rebuilds.
    private int _lastDotsCompleted = -1;
    private int _lastDotsTotal = -1;

    // H3: first-run pulse storyboard reference so we can stop it on Start.
    private Storyboard? _firstRunPulse;

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
        // L1: resolve the muted idle brush from the theme token so it tracks
        // any future theme updates rather than being hard-coded to the old #B7AEC2.
        if (TryFindResource("Brush.Text.Muted") is Brush muted)
            _idleBrush = muted;

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

        // H3: show first-run nudge if this is the user's first session.
        if (!_settings.FirstRunCompleted)
        {
            FirstRunCaption.Visibility = Visibility.Visible;
            StartFirstRunPulse();
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

    /// <summary>
    /// H1: rebuild the cycle-position dot row when (completed, total) has changed.
    /// Filled dots up to completed, hollow dots beyond. Hidden when phase is Idle
    /// or a break (dots only make sense in a Focus context).
    /// </summary>
    public void UpdateCycleDots(int completed, int total, Phase phase)
    {
        // Only show dots while in a Focus (or AwaitingReturn) phase;
        // hide during Idle and actual breaks.
        bool showDots = phase is Phase.Focus or Phase.AwaitingReturn or Phase.ShortBreak or Phase.LongBreak
                        && total > 1; // single-block cycles have no progress to show

        if (!showDots)
        {
            CycleDotsPanel.Visibility = Visibility.Collapsed;
            _lastDotsCompleted = -1;
            _lastDotsTotal = -1;
            return;
        }

        // Avoid redundant rebuilds.
        if (completed == _lastDotsCompleted && total == _lastDotsTotal)
        {
            CycleDotsPanel.Visibility = Visibility.Visible;
            return;
        }

        _lastDotsCompleted = completed;
        _lastDotsTotal = total;

        CycleDotsPanel.Items.Clear();
        for (int i = 0; i < total; i++)
        {
            bool filled = i < completed;
            // Reuse the PhaseDot motif: 9px ellipse, phase accent when filled, muted outline when hollow.
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, i < total - 1 ? 5 : 0, 0)
            };

            if (filled)
            {
                // Filled: use the focus accent brush.
                dot.Fill = FocusBrush;
                dot.Opacity = 0.9;
            }
            else
            {
                // Hollow: transparent fill, muted stroke ring.
                dot.Fill = System.Windows.Media.Brushes.Transparent;
                dot.Stroke = _idleBrush;
                dot.StrokeThickness = 1.2;
                dot.Opacity = 0.45;
            }

            CycleDotsPanel.Items.Add(dot);
        }

        CycleDotsPanel.Visibility = Visibility.Visible;
    }

    // ---- H3: first-run nudge pulse ----

    /// <summary>
    /// H3: start a single gentle opacity pulse on the StartButton to draw attention.
    /// Runs only when AnimationsEnabled; falls back to a static muted-opacity hint.
    /// Pulse stops automatically and is cleared when CompleteFirstRun() is called.
    /// </summary>
    private void StartFirstRunPulse()
    {
        if (!AnimationsEnabled)
        {
            // Reduced motion: just ensure StartButton is at full opacity.
            StartButton.Opacity = 1.0;
            return;
        }

        // Single slow pulse: 1.0 -> 0.55 -> 1.0 once (~0.9s each way), then holds.
        // Not looped so it doesn't become distracting.
        var pulse = new DoubleAnimation(1.0, 0.55, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(1),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        pulse.Completed += (_, _) => StartButton.Opacity = 1.0;

        _firstRunPulse = new Storyboard();
        _firstRunPulse.Children.Add(pulse);
        Storyboard.SetTarget(pulse, StartButton);
        Storyboard.SetTargetProperty(pulse, new PropertyPath(UIElement.OpacityProperty));
        _firstRunPulse.Begin(this);
    }

    /// <summary>
    /// H3: called when the user presses Start for the first time.
    /// Stops the nudge pulse, hides the caption, and marks first-run done in settings.
    /// </summary>
    public void CompleteFirstRun()
    {
        if (_settings.FirstRunCompleted) return;

        _settings.FirstRunCompleted = true;
        _settings.Save();

        _firstRunPulse?.Stop(this);
        _firstRunPulse = null;
        StartButton.Opacity = 1.0;
        FirstRunCaption.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// H2: animate a brief accent bloom on the PhaseDot when a genuine focus block
    /// completes (focus -> break transition). Reuses CubicEase at ~0.4s, gates on
    /// AnimationsEnabled. The dot briefly brightens via opacity spike then settles.
    /// </summary>
    public void PlayCompletionBloom()
    {
        if (!AnimationsEnabled) return;

        // Quick brightness spike: 1.0 -> 0.3 -> 1.0 in ~0.4s total.
        var bloom = new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(0.18))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(1),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        bloom.Completed += (_, _) => PhaseDot.Opacity = 1.0;
        PhaseDot.BeginAnimation(UIElement.OpacityProperty, bloom);
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
                // L2: colon replaces the banned em dash.
                // M2: AutomationProperties.Name carries the clean phrase for screen readers.
                PhaseLabel.Text = "Idle: press Start";
                AutomationProperties.SetName(PhaseLabel, "Idle: press Start");
                PhaseLabel.Foreground = _idleBrush; // L1: from theme token
                PhaseDot.Fill = _idleBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.Focus:
                // M2: visual text is tracked-out ("F O C U S"); a11y name is clean.
                PhaseLabel.Text = TrackOut("Focus");
                AutomationProperties.SetName(PhaseLabel, "Focus");
                PhaseLabel.Foreground = FocusBrush;
                PhaseDot.Fill = FocusBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.ShortBreak:
                PhaseLabel.Text = TrackOut("Short break");
                AutomationProperties.SetName(PhaseLabel, "Short break");
                PhaseLabel.Foreground = ShortBreakBrush;
                PhaseDot.Fill = ShortBreakBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.LongBreak:
                PhaseLabel.Text = TrackOut("Long break");
                AutomationProperties.SetName(PhaseLabel, "Long break");
                PhaseLabel.Foreground = LongBreakBrush;
                PhaseDot.Fill = LongBreakBrush;
                TimeLabel.Foreground = PrimaryTextBrush;
                break;
            case Phase.AwaitingReturn:
                PhaseLabel.Text = TrackOut("Break over");
                AutomationProperties.SetName(PhaseLabel, "Break over");
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

    /// <summary>
    /// Determine whether looping animations should run.
    /// Suppressed when the user toggled ReduceMotion OR when the OS has
    /// ClientAreaAnimation disabled (System > Accessibility > Visual effects).
    /// </summary>
    private bool AnimationsEnabled =>
        !_settings.ReduceMotion && SystemParameters.ClientAreaAnimation;

    private void StartDotPulse()
    {
        // H2: honour ReduceMotion and OS ClientAreaAnimation.
        if (!AnimationsEnabled)
        {
            // Render the dot at a calm steady opacity instead of pulsing.
            PhaseDot.Opacity = 0.7;
            return;
        }

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
