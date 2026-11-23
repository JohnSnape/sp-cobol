// Windows Forms renderer: the same SCREEN SECTION, shown in a window.
//
// The text of the screen (literals, output fields, colors) is painted on a
// character-cell grid, so the layout matches the console version exactly.
// During an ACCEPT each input field becomes a real TextBox at its LINE/COL,
// and a button bar offers Enter, Esc and (with CRT STATUS) the function keys.
//
// The COBOL program runs on its own thread; the window has its own UI thread.
// ACCEPT blocks the program thread until the user presses Enter, Esc, a
// function key, or one of the buttons -- the same model as the console.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CobolDotNet.Runtime;

namespace CobolDotNet.Forms;

public sealed class FormsRenderer : IScreenRenderer
{
    readonly ScreenForm form;

    public FormsRenderer(string title)
    {
        title ??= System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var ready = new ManualResetEventSlim();
        ScreenForm created = null;
        var ui = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            created = new ScreenForm(title);
            created.Shown += (s, e) => ready.Set();
            Application.Run(created);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Name = "COBOL screen window";
        ui.Start();
        ready.Wait();
        form = created;
    }

    public bool OwnsConsoleOutput => true;

    public void Present(ScreenGrid grid)
    {
        var snap = grid.Clone();
        form.BeginInvoke(() => form.ShowGrid(snap));
    }

    public int Accept(AcceptRequest request)
    {
        var done = new TaskCompletionSource<int>();
        var snap = request.Grid.Clone();
        form.BeginInvoke(() => { form.ShowGrid(snap); form.BeginAccept(request, done); });
        return done.Task.Result;
    }

    public void Bell() => SystemSounds.Beep.Play();

    public void Shutdown(ScreenGrid grid, bool waitForUser)
    {
        Present(grid);
        if (waitForUser)
        {
            var done = new TaskCompletionSource<int>();
            form.BeginInvoke(() => form.WaitForClose(done));
            done.Task.Wait();
        }
        form.BeginInvoke(() => form.Finish());
    }
}

/// <summary>Starts a COBOL program with the Windows renderer.</summary>
public static class FormsHost
{
    public static void Run(Action<string[]> main, string[] args, string title)
    {
        Cob.ScreenRenderer = new FormsRenderer(title);
        try
        {
            main(args);
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;
            MessageBox.Show(inner.Message, $"{title} stopped with an error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }
    }
}

sealed class ScreenForm : Form
{
    static readonly Color[] Palette =
    {
        Color.FromArgb(0, 0, 0), Color.FromArgb(0, 0, 170), Color.FromArgb(0, 170, 0), Color.FromArgb(0, 170, 170),
        Color.FromArgb(170, 0, 0), Color.FromArgb(170, 0, 170), Color.FromArgb(170, 85, 0), Color.FromArgb(170, 170, 170),
        Color.FromArgb(85, 85, 85), Color.FromArgb(85, 85, 255), Color.FromArgb(85, 255, 85), Color.FromArgb(85, 255, 255),
        Color.FromArgb(255, 85, 85), Color.FromArgb(255, 85, 255), Color.FromArgb(255, 255, 85), Color.FromArgb(255, 255, 255),
    };

    readonly GridPanel panel;
    readonly FlowLayoutPanel bar;
    readonly Label status;
    readonly Button enterButton, escButton;
    readonly List<Button> fkeyButtons = new();
    readonly Font cellFont;
    readonly int cellW, cellH;

    AcceptRequest req;
    TaskCompletionSource<int> pending;
    readonly List<TextBox> boxes = new();
    bool finishing, programDone;

    public ScreenForm(string title)
    {
        Text = title;
        cellFont = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Point);
        var size = TextRenderer.MeasureText("WWWWWWWWWW", cellFont, Size.Empty, TextFormatFlags.NoPadding);
        cellW = (int)Math.Ceiling(size.Width / 10.0);
        cellH = cellFont.Height + 2;

        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = SystemColors.Control;

        panel = new GridPanel(this) { Location = new Point(0, 0) };
        panel.Size = new Size(ScreenGrid.DefaultColumns * cellW, ScreenGrid.DefaultRows * cellH);
        Controls.Add(panel);

        bar = new FlowLayoutPanel
        {
            Location = new Point(0, panel.Bottom),
            Size = new Size(panel.Width, 36),
            Padding = new Padding(6, 4, 6, 4),
            WrapContents = false,
        };
        enterButton = MakeButton("Enter", CobKey.Enter, validate: true);
        escButton = MakeButton("Esc", CobKey.Escape, validate: false);
        bar.Controls.Add(enterButton);
        bar.Controls.Add(escButton);
        for (int n = 1; n <= 10; n++)
        {
            var b = MakeButton("F" + n, CobKey.F(n), validate: false);
            b.Width = 40;
            b.Visible = false;
            fkeyButtons.Add(b);
            bar.Controls.Add(b);
        }
        status = new Label { AutoSize = true, ForeColor = Color.DarkRed, Padding = new Padding(8, 6, 0, 0) };
        bar.Controls.Add(status);
        Controls.Add(bar);

        ClientSize = new Size(panel.Width, bar.Bottom);
        SetAccepting(false);
    }

    Button MakeButton(string text, int key, bool validate)
    {
        var b = new Button { Text = text, Width = 64, Height = 26, CausesValidation = validate, TabStop = false };
        b.Click += (s, e) => Complete(key);
        return b;
    }

    void SetAccepting(bool on)
    {
        enterButton.Enabled = on || programDone;
        escButton.Enabled = on;
        foreach (var b in fkeyButtons) b.Enabled = on;
    }

    // ---------------------------------------------------------------- grid

    internal ScreenGrid Grid;

    public void ShowGrid(ScreenGrid g)
    {
        Grid = g;
        panel.Invalidate();
    }

    sealed class GridPanel : Control
    {
        readonly ScreenForm f;
        public GridPanel(ScreenForm f)
        {
            this.f = f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = f.Grid;
            var gr = e.Graphics;
            if (g == null) { gr.Clear(Color.Black); return; }
            gr.Clear(Palette[g.ScreenBg & 7]);
            for (int r = 0; r < g.Rows; r++)
            {
                for (int c = 0; c < g.Columns; c++)
                {
                    var cell = g.Cells[r, c];
                    var rect = new Rectangle(c * f.cellW, r * f.cellH, f.cellW, f.cellH);
                    using (var bg = new SolidBrush(Palette[cell.Bg & 7])) gr.FillRectangle(bg, rect);
                    char ch = cell.Ch;
                    if (ch > ' ')
                        TextRenderer.DrawText(gr, ch.ToString(), f.cellFont, rect, Palette[cell.Fg & 15],
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    if (cell.Underline)
                        using (var p = new Pen(Palette[cell.Fg & 15]))
                            gr.DrawLine(p, rect.Left, rect.Bottom - 2, rect.Right, rect.Bottom - 2);
                }
            }
        }
    }

    // ---------------------------------------------------------------- ACCEPT

    public void BeginAccept(AcceptRequest r, TaskCompletionSource<int> done)
    {
        req = r;
        pending = done;
        foreach (var b in fkeyButtons) b.Visible = r.FunctionKeys;
        status.Text = r.Message ?? "";

        for (int i = 0; i < r.Inputs.Count; i++)
        {
            var inp = r.Inputs[i];
            var tb = new TextBox
            {
                Font = cellFont,
                BorderStyle = BorderStyle.FixedSingle,
                AutoSize = false,
                Location = new Point(inp.Col * cellW - 1, inp.Row * cellH),
                Size = new Size(inp.Length * cellW + 4, cellH),
                MaxLength = inp.Length,
                Text = inp.Text.Trim(),
                TabIndex = i,
                Tag = inp,
                UseSystemPasswordChar = inp.Secure,
                CharacterCasing = inp.Upper ? CharacterCasing.Upper : inp.Lower ? CharacterCasing.Lower : CharacterCasing.Normal,
                TextAlign = inp.Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            tb.Validating += (s, e) =>
            {
                if (pending == null || finishing) return;
                e.Cancel = !CheckBox(tb);
            };
            // the first key typed replaces the old contents (as on the console). Select
            // now and again once focus has settled: a full field (MaxLength) would
            // otherwise swallow keys typed right after an AUTO skip.
            tb.Enter += (s, e) => { tb.SelectAll(); BeginInvoke(() => tb.SelectAll()); };
            if (inp.Auto)
            {
                tb.TextChanged += (s, e) =>
                {
                    if (tb.Text.Length < inp.Length || !tb.Focused) return;
                    if (tb.TabIndex == boxes.Count - 1) { Complete(CobKey.Enter); return; }
                    int next = tb.TabIndex + 1;
                    if (CheckBox(tb)) { boxes[next].Focus(); boxes[next].SelectAll(); }
                };
            }
            boxes.Add(tb);
            panel.Controls.Add(tb);
        }
        SetAccepting(true);
        Activate();
        if (boxes.Count > 0) boxes[Math.Clamp(r.Focus, 0, boxes.Count - 1)].Focus();
    }

    /// <summary>Normalize one field (numbers get their PICTURE format). False = stay in it.</summary>
    bool CheckBox(TextBox tb)
    {
        var inp = (ScreenInput)tb.Tag;
        string text = tb.Text.PadRight(inp.Length).Substring(0, inp.Length);
        if (req.Check == null) return true;
        var (ok, error) = req.Check(inp, text);
        if (error != null)
        {
            SystemSounds.Beep.Play();
            status.Text = error;
            tb.SelectAll();
            return false;
        }
        status.Text = "";
        if (inp.Numeric) tb.Text = ok.Trim();
        return true;
    }

    void Complete(int key)
    {
        if (programDone && key == CobKey.Enter) { pending?.TrySetResult(0); return; }
        if (pending == null || req == null) return;
        if (key == CobKey.Enter && ActiveControl is TextBox active && boxes.Contains(active) && !CheckBox(active))
            return;
        finishing = true;
        for (int i = 0; i < boxes.Count; i++)
        {
            var inp = (ScreenInput)boxes[i].Tag;
            inp.Text = boxes[i].Text.PadRight(inp.Length).Substring(0, inp.Length);
            if (boxes[i].Focused) req.Focus = i;
        }
        foreach (var tb in boxes) { panel.Controls.Remove(tb); tb.Dispose(); }
        boxes.Clear();
        finishing = false;
        status.Text = "";
        SetAccepting(false);
        var p = pending;
        pending = null;
        req = null;
        p.TrySetResult(key);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (pending != null)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k >= Keys.F1 && k <= Keys.F24 && (keyData & (Keys.Alt | Keys.Control)) == 0)
            {
                Complete(CobKey.F(k - Keys.F1 + 1));
                return true;
            }
            switch (keyData)
            {
                case Keys.Enter: Complete(CobKey.Enter); return true;
                case Keys.Escape: if (!programDone) Complete(CobKey.Escape); return true;
                case Keys.PageUp: if (!programDone) Complete(CobKey.PageUp); return true;
                case Keys.PageDown: if (!programDone) Complete(CobKey.PageDown); return true;
                case Keys.Down: SelectNextControl(ActiveControl, true, true, false, true); return true;
                case Keys.Up: SelectNextControl(ActiveControl, false, true, false, true); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- end of program

    public void WaitForClose(TaskCompletionSource<int> done)
    {
        programDone = true;
        pending = done;
        foreach (var b in fkeyButtons) b.Visible = false;
        escButton.Visible = false;
        enterButton.Text = "Close";
        SetAccepting(false);
        status.ForeColor = SystemColors.ControlText;
        status.Text = "The program has finished. Press Enter or close the window.";
        Activate();
    }

    public void Finish()
    {
        programDone = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (programDone)
        {
            pending?.TrySetResult(0);
            return;
        }
        // closing the window while the program runs ends the program, like closing a console
        if (e.CloseReason == CloseReason.UserClosing) Environment.Exit(0);
    }
}
