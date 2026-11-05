using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 回归：图片文件拖进来必须变成 <see cref="Kind.Image"/> 并生成缩略图。
/// 曾经的行为是——所有文件一律走 <c>AddFileAsync</c> 建成 <see cref="Kind.File"/>、thumbName 为 null，
/// 卡片区只画文件名，用户看到的就是"图片不显示预览"。
/// </summary>
public class ItemStoreImageTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public ItemStoreImageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "screenleash-test-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AddFileAsync_图片文件_生成Image卡片且有缩略图()
    {
        var png = Path.Combine(_root, "shot.png");
        using (var bitmap = new Bitmap(16, 16))
        {
            bitmap.Save(png, ImageFormat.Png);
        }

        using var thumbnails = new Thumbnailer(() => 96);
        using var store = new ItemStore(_paths, thumbnails);

        var item = await store.AddFileAsync(png, EdgeKind.Right);

        Assert.Equal(Kind.Image, item.Kind);
        Assert.False(string.IsNullOrEmpty(item.ThumbName), "图片卡片必须有缩略图名，否则卡片区只显示文件名");

        var thumbPath = _paths.ThumbPath(item.ThumbName);
        Assert.NotNull(thumbPath);
        Assert.True(File.Exists(thumbPath), "缩略图文件应已落盘");
    }

    [Fact]
    public async Task AddFileAsync_非图片文件_生成File卡片且无缩略图()
    {
        var txt = Path.Combine(_root, "note.txt");
        await File.WriteAllTextAsync(txt, "hello");

        using var thumbnails = new Thumbnailer(() => 96);
        using var store = new ItemStore(_paths, thumbnails);

        var item = await store.AddFileAsync(txt, EdgeKind.Right);

        Assert.Equal(Kind.File, item.Kind);
        Assert.Null(item.ThumbName);
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPG", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.gif", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.pdf", false)]
    [InlineData("noext", false)]
    public void IsImageFile_按扩展名判断_大小写不敏感(string fileName, bool expected)
    {
        Assert.Equal(expected, ItemStore.IsImageFile(fileName));
    }
}
