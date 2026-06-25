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
using System.Media;
using FocusFlow.Core;
using FocusFlow.Models;

namespace FocusFlow.Services;

/// <summary>
/// Plays the phase-change cue (toast is delegated to TrayIconService) and the
/// chime sound. Falls back to SystemSounds.Asterisk if the bundled WAV is gone.
/// </summary>
public sealed class Notifier
{
    private readonly TrayIconService _tray;
    private Settings _settings;
    private readonly string _chimePath;
    private SoundPlayer? _player;

    public Notifier(TrayIconService tray, Settings settings)
    {
        _tray = tray;
        _settings = settings;
        _chimePath = Path.Combine(AppContext.BaseDirectory, "Assets", "chime.wav");

        if (File.Exists(_chimePath))
        {
            try
            {
                _player = new SoundPlayer(_chimePath);
                _player.LoadAsync();
            }
            catch
            {
                _player = null;
            }
        }
    }

    public void UpdateSettings(Settings settings) => _settings = settings;

    /// <summary>Toast + chime for a phase change.</summary>
    public void NotifyPhase(Phase phase)
    {
        (string title, string text) = MessageFor(phase);
        if (!string.IsNullOrEmpty(title))
        {
            _tray.ShowBalloon(title, text);
        }

        if (_settings.SoundEnabled)
        {
            PlayChime();
        }
    }

    private void PlayChime()
    {
        try
        {
            if (_player is not null)
            {
                _player.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch
        {
            try { SystemSounds.Asterisk.Play(); } catch { /* ignore */ }
        }
    }

    private static (string title, string text) MessageFor(Phase phase) => phase switch
    {
        Phase.Focus => ("Focus time", "Heads down — back to deep work."),
        Phase.ShortBreak => ("Break time", "Short break. Stand up, look away, breathe."),
        Phase.LongBreak => ("Break time", "Long break. Step away and recharge."),
        Phase.AwaitingReturn => ("Break's over", "Click 'I'm back at my desk' to start focusing."),
        _ => (string.Empty, string.Empty)
    };
}
