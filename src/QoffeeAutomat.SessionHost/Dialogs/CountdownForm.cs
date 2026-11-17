using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace QoffeeAutomat.SessionHost.Dialogs;

/// <summary>
/// The window of the countdown (PLAN.md S11, T715): topmost, in the middle of the screen, with the headline, the message, the remaining
/// time and a cancel button. Only the cancel button (and Esc, which presses it) cancels: closing the window in another way, for
/// example with Alt+F4, is refused, because an action that was asked for from far away must not be stopped by accident. The window
/// ends by itself when the time is over, and the agent can close it from any thread (<see cref="Dismiss"/>).
/// </summary>
internal sealed class CountdownForm : Form
{
    private static readonly Size MaxTextSize = new(420, 0);

    private readonly TimeSpan _duration;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Label _remaining;
    private readonly System.Windows.Forms.Timer _ticker;
    private readonly Font _headlineFont;
    private readonly Font _remainingFont;
    private volatile bool _dismissed;
    private bool _ending;
    private string _shown = string.Empty;

    public CountdownForm(CountdownDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _duration = request.Duration;

        Text = CountdownText.Caption(request.Title);
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont ?? Font;
        _headlineFont = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold);
        _remainingFont = new Font(Font.FontFamily, Font.Size * 2.4f, FontStyle.Regular);

        var headline = new Label
        {
            Text = request.Title,
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = MaxTextSize,
            Font = _headlineFont,
            Margin = new Padding(0, 0, 0, 8),
        };
        var body = new Label
        {
            Text = CountdownText.Body(request.Title, request.Message),
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = MaxTextSize,
            Margin = new Padding(0, 0, 0, 12),
        };
        _remaining = new Label
        {
            AutoSize = true,
            UseMnemonic = false,
            Font = _remainingFont,
            Margin = new Padding(0, 0, 0, 12),
        };
        var cancel = new Button
        {
            Text = CountdownText.CancelButton,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Padding = new Padding(12, 2, 12, 2),
        };
        cancel.Click += (_, _) => End(CountdownDialogOutcome.Cancelled);
        AcceptButton = cancel;
        CancelButton = cancel;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(headline, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(_remaining, 0, 2);
        layout.Controls.Add(cancel, 0, 3);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);

        _ticker = new System.Windows.Forms.Timer { Interval = 200 };
        _ticker.Tick += (_, _) => Tick();
        ShowRemaining();
    }

    /// <summary>How the window ended; <see cref="CountdownDialogOutcome.Closed"/> until the user or the clock ends it.</summary>
    public CountdownDialogOutcome Outcome { get; private set; } = CountdownDialogOutcome.Closed;

    /// <summary>Closes the window; callable from any thread, before or after the window exists.</summary>
    public void Dismiss()
    {
        _dismissed = true;
        try
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => End(CountdownDialogOutcome.Closed));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing already.
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_dismissed)
        {
            End(CountdownDialogOutcome.Closed);
            return;
        }

        _ticker.Start();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.UserClosing && !_ending)
        {
            e.Cancel = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ticker.Dispose();
            _headlineFont.Dispose();
            _remainingFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Tick()
    {
        if (Stopwatch.GetElapsedTime(_started) >= _duration)
        {
            End(CountdownDialogOutcome.Elapsed);
            return;
        }

        ShowRemaining();
    }

    private void ShowRemaining()
    {
        string text = CountdownText.Remaining(_duration - Stopwatch.GetElapsedTime(_started));
        if (!string.Equals(text, _shown, StringComparison.Ordinal))
        {
            _shown = text;
            _remaining.Text = text;
        }
    }

    private void End(CountdownDialogOutcome outcome)
    {
        if (_ending)
        {
            return;
        }

        _ending = true;
        Outcome = outcome;
        _ticker.Stop();
        Close();
    }
}
