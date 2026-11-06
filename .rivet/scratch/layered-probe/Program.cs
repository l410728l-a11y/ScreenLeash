using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LayeredProbe;

/// <summary>
/// 探针目标：验证「WS_EX_LAYERED + UpdateLayeredWindow（每像素 alpha）」
/// 的窗口能否在保持半透明的同时，接收到 OLE 拖放（OnDragEnter）。
///
/// 背景：主程序用的是 TransparencyKey，透明像素会让鼠标/拖放穿透，
/// 所以必须在左键按下时取消透明（SetDropOpaque），代价是单击图标就变黑块。
/// 若 layered window 能同时透明 + 收拖放，就能彻底去掉那个开关。
///
/// 跑法：dotnet run --project .rivet/scratch/layered-probe
/// 然后把任意文件拖到那个半透明方块上，看控制台/日志是否打印 DRAG_ENTER。
/// </summary>
internal static class Program
{
    private const string LogPath = @"D:\sky\tianshu\工作区\project011 电脑小挂件\.rivet\scratch\layered-probe\probe.log";

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(LogPath, "PROBE_START\n");
        Application.Run(new ProbeForm());
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\n");
        }
        catch
        {
            // 探针只求结果，写不进去就算了。
        }
    }
}

internal sealed class ProbeForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public ProbeForm()
    {
        Text = "LayeredProbe";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(200, 200, 320, 320);
        TopMost = true;
        ShowInTaskbar = false;
        AllowDrop = true;   // 关键：要能收 OLE 拖放
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 加上 WS_EX_LAYERED；保留 NOACTIVATE（跟主程序一致）。
        var ex = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        Program.Log("HANDLE_CREATED allowDrop=" + AllowDrop);
        RenderLayered();
    }

    /// <summary>用 UpdateLayeredWindow 画一个"完全不透明但四周渐隐"的方块。</summary>
    private void RenderLayered()
    {
        var w = Width;
        var h = Height;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            // 中间实心、边缘半透明：如果这种像素能收拖放，就说明 alpha 不影响接收。
            using var brush = new SolidBrush(Color.FromArgb(230, 30, 120, 220));
            g.FillRectangle(brush, 20, 20, w - 40, h - 40);

            using var pen = new Pen(Color.FromArgb(255, 255, 220, 90), 4);
            g.DrawRectangle(pen, 20, 20, w - 41, h - 41);
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memDc, hBitmap);

        var size = new SIZE { CX = w, CY = h };
        var src = new POINT { X = 0, Y = 0 };
        var dst = new POINT { X = Left, Y = Top };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        var ok = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);

        SelectObject(memDc, oldBitmap);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        Program.Log("RENDER_LAYERED ok=" + ok);
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        drgevent.Effect = DragDropEffects.Copy;
        Program.Log("DRAG_ENTER formats=[" + string.Join("|", drgevent.Data?.GetFormats() ?? Array.Empty<string>()) + "]");
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        drgevent.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        Program.Log("DRAG_LEAVE");
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        Program.Log("DRAG_DROP => LAYERED_WINDOW_RECEIVES_DROP");
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Program.Log("MOUSE_DOWN button=" + e.Button);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // 点一下关闭，方便退出。
        if (e.Button == MouseButtons.Right)
        {
            Program.Log("CLOSE_BY_RIGHT_CLICK");
            Close();
        }
    }
}
