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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusFlow.Core;
using WinForms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ScaleTransform = System.Windows.Media.ScaleTransform;

namespace FocusFlow.UI;

/// <summary>
/// Full-screen dim overlay for a single monitor. Positioned with Win32
/// SetWindowPos using the monitor's PHYSICAL-pixel bounds, which sidesteps
/// DIP conversion and is correct on mixed-DPI multi-monitor setups (the window
/// is PerMonitorV2-aware per app.manifest).
/// </summary>
public partial class BreakOverlayWindow : Window
{
    private bool _secondScale;
    // H2: gate looping animations on ReduceMotion setting + OS ClientAreaAnimation.
    private bool _reduceMotion;

    private DateTime? _awaitingSince;
    private DispatcherTimer? _awaitingTicker;

    public event Action? BackClicked;
    public event Action? ExtendClicked;
    public event Action? ConfirmClicked;
    public event Action? PostponeClicked;

    // Win32 interop for exact physical-pixel placement.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly WinForms.Screen _screen;

    public BreakOverlayWindow(WinForms.Screen screen, string reflectionPrompt, bool secondScale, Phase phase, bool reduceMotion = false)
    {
        InitializeComponent();
        _screen = screen;
        _secondScale = secondScale;
        _reduceMotion = reduceMotion;
        ReflectionText.Text = string.IsNullOrEmpty(reflectionPrompt)
            ? "Step back — are you still working on the right thing?"  // L5: fallback
            : reflectionPrompt;

        // The breathing ring must wear the PHASE accent (amber short / rose long),
        // not the hard-coded sage. One accent -> two cohesive shades for the ring
        // gradient, plus glow + inner-fill + awaiting label.
        ApplyPhaseAccent(phase);

        // Place provisionally in DIPs so the window has a sane initial location
        // before the HWND exists; refined in SourceInitialized via SetWindowPos.
        Left = 0;
        Top = 0;
        Width = 200;
        Height = 200;

        SourceInitialized += OnSourceInitialized;
        // Keyboard affordance for the full-screen modal (mouse no longer forced).
        // Routes Enter/Esc to the SAME actions as the buttons — no new behavior.
        KeyDown += OnKeyDown;
        // H2: stop looping animations when ReduceMotion or OS ClientAreaAnimation
        // is off. Runs after the XAML Loaded trigger fires the entrance storyboard,
        // then immediately cancels the forever-looping ring/inner-fill segments.
        Loaded += OnLoadedAnimationGate;
    }

    private bool _confirmShown;

    /// <summary>
    /// Keyboard shortcuts that mirror the on-screen buttons exactly:
    ///   Enter  -> confirm "I'm back" (only once the confirm control is shown)
    ///   Esc    -> skip the break (the "Skip break" ghost button)
    /// These invoke the identical events the Click handlers raise; they do not
    /// change any underlying confirm/skip logic.
    /// </summary>
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                if (_confirmShown)
                {
                    ConfirmClicked?.Invoke();
                    e.Handled = true;
                }
                break;
            case System.Windows.Input.Key.Escape:
                if (!_confirmShown)
                {
                    BackClicked?.Invoke();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// Drive every accent surface of the overlay from the single phase accent.
    /// Short break -> amber, long break -> rose (matching the mini timer). The
    /// ring gradient's bright/dark stops are derived from the one accent so the
    /// ring stays cohesive (one accent, two shades).
    /// </summary>
    private void ApplyPhaseAccent(Phase phase)
    {
        Color accent = phase == Phase.LongBreak
            ? (Color)FindResource("Color.Accent.Long")    // rose
            : (Color)FindResource("Color.Accent.Short");  // amber (short break)

        Color bright = Lighten(accent, 0.34);   // top-left ring highlight
        Color deep = Darken(accent, 0.30);      // bottom-right ring shadow

        // Ring stroke gradient: bright -> deep, both from the one accent.
        RingStop0.Color = bright;
        RingStop1.Color = deep;

        // Outer glow ring (radial): solid accent fading to transparent accent.
        GlowStop0.Color = accent;
        GlowStop1.Color = Transparent(accent);

        // Soft inner fill: accent center fading to transparent accent.
        InnerFillStop.Color = accent;
        InnerFillStop1.Color = Transparent(accent);

        // "Break ended X ago" accountability label carries the accent too.
        AwaitingSinceLabel.Foreground = new SolidColorBrush(accent);
    }

    private static Color Lighten(Color c, double amount)
    {
        byte L(byte ch) => (byte)Math.Clamp(ch + (255 - ch) * amount, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    private static Color Darken(Color c, double amount)
    {
        byte D(byte ch) => (byte)Math.Clamp(ch * (1 - amount), 0, 255);
        return Color.FromRgb(D(c.R), D(c.G), D(c.B));
    }

    private static Color Transparent(Color c) => Color.FromArgb(0, c.R, c.G, c.B);

    /// <summary>
    /// H2: after the XAML entrance storyboard fires, stop any forever-looping
    /// animations when ReduceMotion is enabled or the OS has ClientAreaAnimation
    /// disabled. The entrance fades are self-terminating and are left untouched —
    /// only the continuous ring-scale and ring-inner-opacity loops are cancelled,
    /// leaving the ring rendered at its resting scale (1.0) and resting opacity.
    /// </summary>
    private void OnLoadedAnimationGate(object? sender, RoutedEventArgs e)
    {
        bool animationsEnabled = !_reduceMotion && SystemParameters.ClientAreaAnimation;
        if (animationsEnabled) return; // nothing to suppress

        // Dispatch slightly after Loaded so the entrance storyboard has started
        // before we cancel only the forever loops. The entrance fades have a
        // BeginTime >= 0.45s so they won't be affected by this immediate cancel.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            // Stop the forever-looping RingScale X/Y animations.
            RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            // Clamp scale to the rest value (1.0) so it doesn't stay at the
            // animation's initial From="1.0" by accident.
            RingScale.ScaleX = 1.0;
            RingScale.ScaleY = 1.0;

            // Stop the forever RingInner opacity loop and settle at rest.
            RingInner.BeginAnimation(UIElement.OpacityProperty, null);
            RingInner.Opacity = 0.08; // resting tone as defined in XAML
        });
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        CoverScreen();
    }

    /// <summary>Cover the assigned monitor exactly using physical-pixel bounds.</summary>
    private void CoverScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var b = _screen.Bounds; // physical pixels
        SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Set the small tracked-out phase label. This is a STATIC short caps label
    /// ("SHORT BREAK" / "LONG BREAK"), so emulated letter-spacing via TrackOut is
    /// appropriate here. Dynamic / prose text must NOT use TrackOut.
    /// M2: AutomationProperties.Name is set to the clean title word so screen
    /// readers do not spell out the inter-character spaces.
    /// </summary>
    public void SetPhaseTitle(string title)
    {
        PhaseTitle.Text = TrackOut(title);
        AutomationProperties.SetName(PhaseTitle, title);
    }

    /// <summary>
    /// Emulated letter-spacing for STATIC short caps labels only. Inserts a hair
    /// space between glyphs. Never use on prose or live-updating numerals.
    /// </summary>
    private static string TrackOut(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length * 2);
        string upper = s.ToUpperInvariant();
        for (int i = 0; i < upper.Length; i++)
        {
            sb.Append(upper[i]);
            if (i < upper.Length - 1) sb.Append(' '); // thin space
        }
        return sb.ToString();
    }

    public void UpdateTime(TimeSpan remaining)
    {
        CountdownLabel.Text = _secondScale && remaining.TotalMinutes < 1
            ? $"{(int)remaining.TotalSeconds:00}"
            : $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackClicked?.Invoke();

    private void OnExtendClick(object sender, RoutedEventArgs e) => ExtendClicked?.Invoke();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => ConfirmClicked?.Invoke();

    private void OnPostponeClick(object sender, RoutedEventArgs e) => PostponeClicked?.Invoke();

    /// <summary>
    /// Reveal the soft-cap nudge text beneath the postpone button. Called by
    /// App.xaml.cs once the engine's PostponeCount reaches PostponeNudgeThreshold.
    /// The button stays enabled — this is advisory only.
    /// </summary>
    public void ShowPostponeNudge()
    {
        PostponeNudge.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Switch the overlay from the live break countdown to the "welcome back /
    /// confirm" state. Crossfades the heading + reflection, eases the ring to a
    /// rest scale, hides the countdown / skip / +5 controls, and reveals the
    /// single "I'm back at my desk" button plus a live "Break ended X ago"
    /// counter.
    /// </summary>
    public void EnterConfirmState(string heading, string message, DateTime awaitingSince)
    {
        // Heading becomes the serif "Welcome back" voice (not tracked caps).
        PhaseTitle.Text = heading;
        PhaseTitle.Style = (Style)FindResource("Display");
        PhaseTitle.FontSize = (double)FindResource("Size.Title");
        PhaseTitle.Foreground = (Brush)FindResource("Brush.Text.Primary");

        ReflectionText.Text = message;

        // Stop the breathing loop and ease the ring to a calm rest scale.
        BeginAnimation_RingToRest();

        // Crossfade controls: the ring & numerals stay airy; only the confirm
        // button is solid. Fade out skip/+5, reveal the primary button.
        CountdownLabel.Visibility = Visibility.Collapsed;
        CrossfadeControls();

        // The break is over and we're inviting the user back, so it's appropriate
        // to bring the overlay forward now (it was shown NOACTIVATE so as not to
        // steal focus mid-break). This lets Enter confirm without forcing a click.
        // It does NOT alter the confirm action — only where keystrokes land.
        _confirmShown = true;
        try { Activate(); ConfirmButton.Focus(); } catch { /* non-fatal */ }

        _awaitingSince = awaitingSince;
        UpdateAwaitingLabel();

        // Orchestrate the accountability label to fade in AFTER the ring has
        // settled to rest (ring rest ~0.8s) — it should not snap in.
        AwaitingSinceLabel.Opacity = 0;
        AwaitingSinceLabel.Visibility = Visibility.Visible;
        var labelFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5))
        {
            BeginTime = TimeSpan.FromSeconds(0.9),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        AwaitingSinceLabel.BeginAnimation(UIElement.OpacityProperty, labelFade);

        _awaitingTicker ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _awaitingTicker.Tick -= OnAwaitingTick;
        _awaitingTicker.Tick += OnAwaitingTick;
        _awaitingTicker.Start();
    }

    /// <summary>
    /// Ease the breathing ring out of its forever-loop into a steady rest scale.
    /// Snapshots the current animated scale, removes the looping animation, then
    /// animates to 1.0 so the transition is smooth rather than a jump.
    /// </summary>
    private void BeginAnimation_RingToRest()
    {
        double currentX = RingScale.ScaleX;
        double currentY = RingScale.ScaleY;

        // Clear the forever-loop, then re-seed from the snapshot.
        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RingInner.BeginAnimation(UIElement.OpacityProperty, null);
        RingScale.ScaleX = currentX;
        RingScale.ScaleY = currentY;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var toRestX = new DoubleAnimation(currentX, 1.0, TimeSpan.FromSeconds(0.8)) { EasingFunction = ease };
        var toRestY = new DoubleAnimation(currentY, 1.0, TimeSpan.FromSeconds(0.8)) { EasingFunction = ease };
        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, toRestX);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, toRestY);

        // Rest the inner fill to its calm baseline (matches XAML resting 0.08).
        var innerToRest = new DoubleAnimation(RingInner.Opacity, 0.08, TimeSpan.FromSeconds(0.8));
        RingInner.BeginAnimation(UIElement.OpacityProperty, innerToRest);
    }

    /// <summary>Fade the during-break controls out and the confirm button in.</summary>
    private void CrossfadeControls()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.25));
        fadeOut.Completed += (_, _) =>
        {
            BreakControls.Visibility = Visibility.Collapsed;
            ConfirmControls.Opacity = 0;
            ConfirmControls.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ConfirmControls.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        BreakControls.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void OnAwaitingTick(object? sender, EventArgs e) => UpdateAwaitingLabel();

    private void UpdateAwaitingLabel()
    {
        if (_awaitingSince is null) return;
        TimeSpan elapsed = DateTime.Now - _awaitingSince.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        int totalSeconds = (int)elapsed.TotalSeconds;
        string ago = totalSeconds < 60
            ? $"{totalSeconds}s ago"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s ago";
        // Rendered as NORMAL text — no per-char space hack. Tabular numerals
        // (set in XAML) keep the live counter from reflowing each second.
        AwaitingSinceLabel.Text = $"Break ended {ago}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _awaitingTicker?.Stop();
        base.OnClosed(e);
    }
}
