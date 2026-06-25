using System.Windows.Threading;
using FocusFlow.Models;

namespace FocusFlow.Core;

public enum Phase
{
    Idle,
    Focus,
    ShortBreak,
    LongBreak,
    /// <summary>
    /// A break has finished but the user has not yet confirmed they are back at
    /// their desk. The timer is stopped and nothing counts; ConfirmReturn()
    /// (or Skip) leaves this state into Focus.
    /// </summary>
    AwaitingReturn
}

/// <summary>
/// Pomodoro-style state machine. Tracks remaining time in SECONDS and ticks
/// once per second via a DispatcherTimer (UI thread). Raises Tick / PhaseChanged
/// / CycleCompleted events.
///
/// Flow: Idle -> Focus -> ShortBreak -> Focus -> ... -> LongBreak (after
/// BlocksPerLongBreak focus blocks) -> Focus -> ...
/// </summary>
public sealed class TimerEngine
{
    private readonly DispatcherTimer _timer;
    private Settings _settings;

    private int _remainingSeconds;
    private int _completedFocusBlocks;

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public bool IsRunning { get; private set; }

    /// <summary>Completed focus blocks since the last long break.</summary>
    public int CompletedFocusBlocks => _completedFocusBlocks;

    /// <summary>
    /// When the engine entered <see cref="Phase.AwaitingReturn"/>. Null otherwise.
    /// Lets the UI show how long the break has been over.
    /// </summary>
    public DateTime? AwaitingSince { get; private set; }

    public TimeSpan Remaining => TimeSpan.FromSeconds(_remainingSeconds);

    /// <summary>Fired every second while running with the remaining time.</summary>
    public event Action<TimeSpan>? Tick;

    /// <summary>Fired when the phase changes (including Idle -> Focus on start).</summary>
    public event Action<Phase>? PhaseChanged;

    /// <summary>Fired when a full cycle completes (a LongBreak finishes).</summary>
    public event Action? CycleCompleted;

    public TimerEngine(Settings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Swap in new settings. Current phase keeps running with its current
    /// remaining time; new durations take effect on the NEXT phase.
    /// </summary>
    public void UpdateSettings(Settings settings) => _settings = settings;

    private int DurationFor(Phase phase) => phase switch
    {
        Phase.Focus => _settings.FocusSeconds,
        Phase.ShortBreak => _settings.ShortBreakSeconds,
        Phase.LongBreak => _settings.LongBreakSeconds,
        _ => 0
    };

    /// <summary>Start from Idle (begins the first Focus block) or resume if paused.</summary>
    public void Start()
    {
        if (CurrentPhase == Phase.Idle)
        {
            EnterPhase(Phase.Focus);
            IsRunning = true;
            _timer.Start();
        }
        else
        {
            Resume();
        }
    }

    public void Pause()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _timer.Stop();
    }

    public void Resume()
    {
        // AwaitingReturn is not a paused state; resuming it means confirming.
        if (CurrentPhase == Phase.AwaitingReturn) { ConfirmReturn(); return; }
        if (IsRunning || CurrentPhase == Phase.Idle) return;
        IsRunning = true;
        _timer.Start();
    }

    /// <summary>Toggle between running and paused (Start if Idle).</summary>
    public void TogglePause()
    {
        if (CurrentPhase == Phase.Idle) { Start(); return; }
        // While awaiting the user's return, the Start/Pause action confirms.
        if (CurrentPhase == Phase.AwaitingReturn) { ConfirmReturn(); return; }
        if (IsRunning) Pause();
        else Resume();
    }

    /// <summary>
    /// Confirm the user is back at their desk: leave AwaitingReturn and start the
    /// next Focus block counting. No-op if not currently AwaitingReturn.
    /// </summary>
    public void ConfirmReturn()
    {
        if (CurrentPhase != Phase.AwaitingReturn) return;
        AwaitingSince = null;
        IsRunning = true;
        EnterPhase(Phase.Focus);
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Advance to the next phase immediately.</summary>
    public void Skip()
    {
        if (CurrentPhase == Phase.Idle)
        {
            Start();
            return;
        }
        // Skipping while awaiting the return is an explicit at-the-desk action.
        if (CurrentPhase == Phase.AwaitingReturn)
        {
            ConfirmReturn();
            return;
        }
        // Skip is an explicit at-the-desk action, so it bypasses the
        // post-break "confirm I'm back" gate and goes straight to Focus.
        // Clear any paused state first: skipping always starts the next phase
        // running, regardless of whether the engine was paused before the skip.
        IsRunning = true;
        AdvancePhase(fromTimer: false);
        // EnterPhase starts the timer only when IsRunning is true, which it now
        // is. Guard against the timer already running (normal-flow skip).
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>Stop and return to Idle, clearing block count.</summary>
    public void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        _completedFocusBlocks = 0;
        _remainingSeconds = 0;
        AwaitingSince = null;
        CurrentPhase = Phase.Idle;
        PhaseChanged?.Invoke(CurrentPhase);
        Tick?.Invoke(Remaining);
    }

    /// <summary>Extend the current phase by 5 minutes (300s).</summary>
    public void AddFiveMinutes()
    {
        // Nothing is counting in Idle or AwaitingReturn, so extending is meaningless.
        if (CurrentPhase is Phase.Idle or Phase.AwaitingReturn) return;
        _remainingSeconds += 5 * 60;
        Tick?.Invoke(Remaining);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        if (_remainingSeconds <= 0)
        {
            AdvancePhase(fromTimer: true);
            return;
        }

        Tick?.Invoke(Remaining);
    }

    /// <summary>
    /// Decide and enter the next phase based on the current one.
    /// </summary>
    /// <param name="fromTimer">
    /// True when invoked because a countdown reached 0; false when the user
    /// explicitly skipped. A break that ends "from the timer" honors the
    /// ConfirmReturnAfterBreak gate; an explicit skip bypasses it.
    /// </param>
    private void AdvancePhase(bool fromTimer)
    {
        switch (CurrentPhase)
        {
            case Phase.Focus:
                _completedFocusBlocks++;
                if (_completedFocusBlocks >= _settings.BlocksPerLongBreak)
                {
                    EnterPhase(Phase.LongBreak);
                }
                else
                {
                    EnterPhase(Phase.ShortBreak);
                }
                break;

            case Phase.ShortBreak:
                EndBreak(fromTimer);
                break;

            case Phase.LongBreak:
                // Long-break bookkeeping must happen at break-end regardless of
                // whether we pause for confirmation, so the next cycle's cadence
                // stays correct.
                _completedFocusBlocks = 0;
                CycleCompleted?.Invoke();
                EndBreak(fromTimer);
                break;

            case Phase.AwaitingReturn:
                // Defensive: any advance out of the waiting state is a confirm.
                ConfirmReturn();
                break;

            case Phase.Idle:
                EnterPhase(Phase.Focus);
                break;
        }
    }

    /// <summary>
    /// Common end-of-break transition. If confirmation is enabled and the break
    /// ended on its own (timer), stop and wait in AwaitingReturn; otherwise go
    /// straight to Focus (today's behavior / explicit skip).
    /// </summary>
    private void EndBreak(bool fromTimer)
    {
        if (fromTimer && _settings.ConfirmReturnAfterBreak)
        {
            EnterAwaitingReturn();
        }
        else
        {
            EnterPhase(Phase.Focus);
        }
    }

    /// <summary>
    /// Stop everything and wait for the user to confirm they are back. Records
    /// the timestamp and fires PhaseChanged(AwaitingReturn). Nothing counts here.
    /// </summary>
    private void EnterAwaitingReturn()
    {
        _timer.Stop();
        IsRunning = false;
        _remainingSeconds = 0;
        AwaitingSince = DateTime.Now;
        CurrentPhase = Phase.AwaitingReturn;
        PhaseChanged?.Invoke(CurrentPhase);
    }

    private void EnterPhase(Phase phase)
    {
        CurrentPhase = phase;
        _remainingSeconds = DurationFor(phase);
        PhaseChanged?.Invoke(phase);
        Tick?.Invoke(Remaining);

        // Keep ticking when a new phase begins via auto-advance.
        if (phase != Phase.Idle && IsRunning && !_timer.IsEnabled)
        {
            _timer.Start();
        }
    }
}
