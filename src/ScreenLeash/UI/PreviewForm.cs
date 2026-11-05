using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using ScreenLeash.Core;
using ScreenLeash.Interop;

namespace ScreenLeash.UI;

/// <summary>
/// 悬停放大预览窗：无边框、置顶、不抢焦点、不进任务栏。
///
/// <para>只缓存**当前这一张**原图（<see cref="Forget"/> 或切换时释放），
/// 因为原图可能有几十 MB；读盘时先读进 <see cref="MemoryStream"/> 再解码，
/// 避免锁住 blobs/ 里的文件（拖出和删除需要能删它）。</para>
/// </summary>
internal sealed class PreviewForm : Form
{
    private Image? _image;
    private string? _loadedPath;

    internal PreviewForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceOpaque;
        DoubleBuffered = true;
        Text = "ScreenLeashPreview";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= InteropConstants.WsExNoActivate;
            parameters.ExStyle |= InteropConstants.WsExToolWindow;
            return parameters;
        }
    }

    /// <summary>显示某张图片的放大预览；卡片没有 blob 或加载失败时自动隐藏。</summary>
    internal void ShowFor(Item item, AppPaths paths, Rectangle anchor, int dpi, Rectangle workingArea)
    {
        if (item is null || item.Kind == Kind.Text)
        {
            HidePreview();
            return;
        }

        var path = paths.BlobPath(item.BlobName);
        if (path is null || !File.Exists(path))
        {
            HidePreview();
            return;
        }

        var image = EnsureImage(path);
        if (image is null)
        {
            HidePreview();
            return;
        }

        var bounds = PreviewGeometry.Compute(anchor, image.Size, workingArea, dpi, item.Edge);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            HidePreview();
            return;
        }

        Bounds = bounds;
        if (!Visible)
        {
            Show();
        }

        Invalidate();
    }

    /// <summary>隐藏预览（保留已解码的图片，方便同一张卡片再次悬停时秒开）。</summary>
    internal void HidePreview()
    {
        if (Visible)
        {
            Hide();
        }
    }

    /// <summary>释放已缓存的图片。</summary>
    internal void Forget()
    {
        _image?.Dispose();
        _image = null;
        _loadedPath = null;
    }

    private Image? EnsureImage(string path)
    {
        if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) && _image is not null)
        {
            return _image;
        }

        _image?.Dispose();
        _image = null;
        _loadedPath = null;

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            _image = new Bitmap(source);
            _loadedPath = path;
            return _image;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return null;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        var client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var surface = new SolidBrush(Theme.SurfaceOpaque))
        {
            g.FillRectangle(surface, client);
        }

        if (_image is not null)
        {
            g.DrawImage(_image, client);
        }

        var radius = Metrics.Px(Metrics.PreviewRadiusDip, DeviceDpi);
        using var path = CardRenderer.RoundedRect(client, radius);
        using var pen = new Pen(Theme.CardBorderActive, Metrics.PxFloat(Metrics.StrokeWidth, DeviceDpi));
        g.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Forget();
        }

        base.Dispose(disposing);
    }
}

/// <summary>预览窗的进程级单例：多条绳子共用一个，避免每个窗口各建一份。</summary>
internal static class PreviewHost
{
    private static PreviewForm? _form;

    internal static bool IsVisible => _form is { Visible: true };

    internal static void Show(Item item, AppPaths paths, Rectangle anchor, int dpi, Rectangle workingArea)
    {
        _form ??= new PreviewForm();
        _form.ShowFor(item, paths, anchor, dpi, workingArea);
    }

    internal static void Hide() => _form?.HidePreview();

    internal static void Release()
    {
        _form?.Dispose();
        _form = null;
    }
}
