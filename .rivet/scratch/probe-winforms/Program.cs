using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LeashProbe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool selfCheck = args.Contains("--selfcheck");
        ApplicationConfiguration.Initialize();

        var form = new LeashProbeForm(selfCheck);
        if (selfCheck)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 3000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                form.Refresh();
                var p = Process.GetCurrentProcess();
                p.Refresh();
                Console.WriteLine($"[probe] workingSet={p.WorkingSet64 / 1048576.0:F1}MB private={p.PrivateMemorySize64 / 1048576.0:F1}MB threads={p.Threads.Count} handles={p.HandleCount}");
                Console.WriteLine($"[probe] bounds={form.Bounds} visible={form.Visible} hwnd=0x{form.Handle.ToInt64():X}");
                Console.WriteLine($"[probe] dropEnabled={form.AllowDrop} topMost={form.TopMost} opacity={form.Opacity}");
                Console.WriteLine("SELFCHECK_OK");
                form.Close();
            };
            timer.Start();
        }

        Application.Run(form);
    }
}

internal sealed class LeashProbeForm : Form
{
    private readonly System.Windows.Forms.Timer _watch;
    private readonly bool _selfCheck;
    private readonly NotifyIcon _tray;
    private readonly Label _dragSource;
    private int _targetWidth = 8;
    private const int CollapsedWidth = 8;
    private const int ExpandedWidth = 148;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    public LeashProbeForm(bool selfCheck)
    {
        _selfCheck = selfCheck;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 36);
        Opacity = 0.93;
        DoubleBuffered = true;
        AllowDrop = true;
        Text = "leash-probe";

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Bounds = new Rectangle(wa.Right - CollapsedWidth, wa.Top, CollapsedWidth, Math.Min(wa.Height, 900));

        DragEnter += (_, e) =>
        {
            Console.WriteLine($"[drag] enter formats={string.Join(",", e.Data!.GetFormats())}");
            e.Effect = DragDropEffects.Copy;
        };
        DragOver += (_, e) => e.Effect = DragDropEffects.Copy;
        DragDrop += (_, e) =>
        {
            Console.WriteLine($"[drag] drop formats={string.Join(",", e.Data!.GetFormats())}");
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                Console.WriteLine($"[drag] files={string.Join("|", (string[])e.Data.GetData(DataFormats.FileDrop)!)}");
            if (e.Data.GetDataPresent(DataFormats.Text))
                Console.WriteLine($"[drag] text={(e.Data.GetData(DataFormats.Text) as string)?.Substring(0, Math.Min(40, (e.Data.GetData(DataFormats.Text) as string)!.Length))}");
            if (e.Data.GetDataPresent(DataFormats.Bitmap))
                Console.WriteLine("[drag] bitmap=yes");
        };

        _dragSource = new Label
        {
            Text = "DRAG ME",
            AutoSize = false,
            Size = new Size(120, 40),
            Location = new Point(4, 40),
            BackColor = Color.OrangeRed,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _dragSource.MouseDown += (_, _) =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "leash-probe.txt");
            File.WriteAllText(tmp, "leash probe payload");
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { tmp });
            data.SetData(DataFormats.UnicodeText, "leash probe payload");
            Console.WriteLine("[drag] DoDragDrop start");
            var eff = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
            Console.WriteLine($"[drag] DoDragDrop end effect={eff}");
        };
        Controls.Add(_dragSource);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "leash-probe",
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Quit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Console.WriteLine("[tray] doubleclick"); };

        _watch = new System.Windows.Forms.Timer { Interval = 120 };
        _watch.Tick += OnWatch;
        _watch.Start();
    }

    private void OnWatch(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out var p)) return;
        var wa = Screen.FromPoint(new Point(p.X, p.Y)).WorkingArea;
        bool nearEdge = p.X >= wa.Right - 44 && p.Y >= wa.Top && p.Y <= wa.Bottom;
        _targetWidth = nearEdge ? ExpandedWidth : CollapsedWidth;
        int w = Bounds.Width;
        if (w != _targetWidth)
        {
            int step = Math.Max(8, Math.Abs(_targetWidth - w) / 3);
            int next = w < _targetWidth ? Math.Min(_targetWidth, w + step) : Math.Max(_targetWidth, w - step);
            SetBounds(wa.Right - next, Bounds.Y, next, Bounds.Height);
        }
        _dragSource.Visible = Bounds.Width > 60;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 2f);
        g.DrawLine(pen, 1, 0, 1, Height);
        if (Bounds.Width > 60)
        {
            using var b = new SolidBrush(Color.FromArgb(24, 26, 32));
            g.FillRectangle(b, 4, 0, Width - 6, Height);
            using var f = new Font("Segoe UI", 8.5f);
            using var tb = new SolidBrush(Color.Gainsboro);
            g.DrawString("leash probe", f, tb, new PointF(8, 8));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _watch?.Dispose(); _tray?.Dispose(); }
        base.Dispose(disposing);
    }
}
