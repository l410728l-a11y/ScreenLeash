using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ScreenLeash.Core;

/// <summary>
/// 用户设置。与 <see cref="ItemStore"/> 的卡片数据分开存放（settings.json），
/// 损坏时整体回退默认值——设置坏了不该让应用起不来。
/// 所有字段都有安全默认值：默认四边全开、全局粘贴兜底开、截图自动挂关、开机自启关。
/// </summary>
public sealed class AppSettings
{
    public const int SchemaVersion = 1;

    public const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupValueName = "ScreenLeash";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public int Version { get; set; } = SchemaVersion;

    // ── 绳子 ─────────────────────────────────────────────────────
    public bool EnableLeft { get; set; } = true;

    public bool EnableTop { get; set; } = true;

    public bool EnableRight { get; set; } = true;

    public bool EnableBottom { get; set; } = true;

    /// <summary>只显示主屏的四条绳；多屏时默认每屏各四条。</summary>
    public bool OnlyPrimaryScreen { get; set; }

    // ── 粘贴与截图 ────────────────────────────────────────────────
    /// <summary>全局 Ctrl+V 兜底（前台无可粘贴目标时把剪贴板挂到绳子）。</summary>
    public bool PasteHookEnabled { get; set; } = true;

    /// <summary>剪贴板出现新截图时自动挂上（默认关，避免干扰正常复制）。</summary>
    public bool ScreenshotAutoPin { get; set; }

    /// <summary>剪贴板监听的节流间隔（毫秒）。</summary>
    public int ClipboardThrottleMs { get; set; } = 500;

    // ── 拖出 ─────────────────────────────────────────────────────
    /// <summary>拖出后把卡片从绳子上移除（默认关：拖出去通常是「用一下」，不是「拿走」）。</summary>
    public bool DeleteAfterDragOut { get; set; }

    /// <summary>拖出用的临时文件在 DoDragDrop 返回后保留多少秒（给微信等异步读取的目标留时间）。</summary>
    public int DragOutRetentionSeconds { get; set; } = 30;

    /// <summary>拖出临时文件保留期的下限（秒）：低于这个值延迟删除就失去意义。</summary>
    public const int MinDragOutRetentionSeconds = 5;

    /// <summary>拖出临时文件保留期的上限（秒）：再长就该由启动清理兜底了。</summary>
    public const int MaxDragOutRetentionSeconds = 600;

    // ── 摄入限制 ─────────────────────────────────────────────────
    /// <summary>单个文件摄入上限（字节）；超过则拒绝并提示。</summary>
    public long MaxSingleFileBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>文本卡片保存的字符数上限。</summary>
    public int TextPreviewChars { get; set; } = 1000;

    // ── 启动 ─────────────────────────────────────────────────────
    public bool StartWithWindows { get; set; }

    public bool IsEdgeEnabled(EdgeKind edge) => edge switch
    {
        EdgeKind.Left => EnableLeft,
        EdgeKind.Top => EnableTop,
        EdgeKind.Right => EnableRight,
        _ => EnableBottom,
    };

    public void SetEdgeEnabled(EdgeKind edge, bool enabled)
    {
        switch (edge)
        {
            case EdgeKind.Left:
                EnableLeft = enabled;
                break;
            case EdgeKind.Top:
                EnableTop = enabled;
                break;
            case EdgeKind.Right:
                EnableRight = enabled;
                break;
            default:
                EnableBottom = enabled;
                break;
        }
    }

    /// <summary>至少保留一条边；否则绳子会整体消失，用户无法再从界面找回。</summary>
    public bool HasAnyEdge => EnableLeft || EnableTop || EnableRight || EnableBottom;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>读设置；文件缺失/损坏/版本越界一律回退默认值，绝不抛。</summary>
    public static AppSettings Load(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.SettingsFile))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(paths.SettingsFile, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null || loaded.Version < 1 || loaded.Version > SchemaVersion)
            {
                return new AppSettings();
            }

            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>原子写：temp → File.Replace。失败只返回 false，不抛（设置保存失败不该打断交互）。</summary>
    public bool Save(AppPaths paths)
    {
        string? temp = null;
        try
        {
            paths.EnsureCreated();
            Version = SchemaVersion;
            Normalize();

            var json = JsonSerializer.Serialize(this, JsonOptions);
            temp = paths.SettingsFile + AppPaths.TempSuffix;
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(paths.SettingsFile))
            {
                File.Replace(temp, paths.SettingsFile, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, paths.SettingsFile);
            }

            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 残留 temp 由下次写入覆盖。
                }
            }
        }
    }

    /// <summary>把越界字段夹回安全范围。</summary>
    public void Normalize()
    {
        MaxSingleFileBytes = Math.Clamp(MaxSingleFileBytes, 1024L * 1024, 2L * 1024 * 1024 * 1024);
        DragOutRetentionSeconds = Math.Clamp(DragOutRetentionSeconds, 5, 600);
        ClipboardThrottleMs = Math.Clamp(ClipboardThrottleMs, 100, 5000);
        TextPreviewChars = Math.Clamp(TextPreviewChars, 64, 10000);
    }

    /// <summary>写入/删除 HKCU Run 开机自启项。返回是否成功（失败不抛）。</summary>
    public static bool ApplyStartupRegistration(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(StartupRegistryKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var path = executablePath ?? Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                key.SetValue(StartupValueName, "\"" + path + "\"");
            }
            else if (key.GetValue(StartupValueName) is not null)
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>当前是否已注册开机自启（用于设置界面回显真实状态）。</summary>
    public static bool IsStartupRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: false);
            return key?.GetValue(StartupValueName) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            return false;
        }
    }
}
