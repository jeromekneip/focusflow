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

using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FocusFlow.Core;

namespace FocusFlow.Services;

/// <summary>
/// Owns the WinForms NotifyIcon: tray icon, tooltip, context menu, and balloon
/// toasts. Translates menu clicks into events the App wires to the engine/UI.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startPauseItem;
    private readonly ToolStripMenuItem _showHideItem;
    private bool _disposed;
    private bool _awaitingReturn;

    public event Action? StartPauseClicked;
    public event Action? SkipClicked;
    public event Action? ToggleMiniTimerClicked;
    public event Action? SettingsClicked;
    public event Action? ExitClicked;

    /// <summary>Raised when the user double-clicks the tray icon while awaiting return.</summary>
    public event Action? ConfirmReturnClicked;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();

        _startPauseItem = new ToolStripMenuItem("Start", null,
            (_, _) => StartPauseClicked?.Invoke());
        _showHideItem = new ToolStripMenuItem("Show/Hide mini timer", null,
            (_, _) => ToggleMiniTimerClicked?.Invoke());

        menu.Items.Add(_startPauseItem);
        menu.Items.Add(new ToolStripMenuItem("Skip phase", null,
            (_, _) => SkipClicked?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showHideItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null,
            (_, _) => SettingsClicked?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null,
            (_, _) => ExitClicked?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "FocusFlow: Idle",
            Visible = true,
            ContextMenuStrip = menu
        };

        // Double-click confirms return while awaiting; otherwise toggles the mini timer.
        _notifyIcon.DoubleClick += (_, _) =>
        {
            if (_awaitingReturn)
                ConfirmReturnClicked?.Invoke();
            else
                ToggleMiniTimerClicked?.Invoke();
        };
    }

    private static Icon LoadIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icoPath))
        {
            try { return new Icon(icoPath); }
            catch { /* fall through */ }
        }
        return SystemIcons.Application;
    }

    /// <summary>Reflect the current running state in the Start/Pause menu label.</summary>
    public void SetRunningState(bool isRunning, Phase phase)
    {
        _awaitingReturn = phase == Phase.AwaitingReturn;
        if (phase == Phase.AwaitingReturn)
            _startPauseItem.Text = "I'm back / Start focus";
        else if (phase == Phase.Idle)
            _startPauseItem.Text = "Start";
        else
            _startPauseItem.Text = isRunning ? "Pause" : "Resume";
    }

    public void SetMiniTimerVisible(bool visible)
    {
        _showHideItem.Text = visible ? "Hide mini timer" : "Show mini timer";
    }

    /// <summary>Update the hover tooltip (max 63 chars for NotifyIcon).</summary>
    public void SetTooltip(string text)
    {
        if (text.Length > 63) text = text[..63];
        _notifyIcon.Text = text;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);
        }
        catch
        {
            // Toast best-effort.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
