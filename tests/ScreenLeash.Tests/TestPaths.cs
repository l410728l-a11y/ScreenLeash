using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ScreenLeash.Tests;

/// <summary>
/// 测试资产定位。路径来自构建期注入的汇编元数据（ScreenLeash.Tests.csproj），
/// 不依赖当前工作目录——从 IDE、dotnet test、pwsh 脚本启动都指向同一处。
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string> RepoRootLazy = new(() => Locate("RepoRoot"));
    private static readonly Lazy<string> FixturesDirLazy = new(() => Locate("FixturesDir"));

    /// <summary>仓库根目录。</summary>
    public static string RepoRoot => RepoRootLazy.Value;

    /// <summary>夹具目录。</summary>
    public static string FixturesDir => FixturesDirLazy.Value;

    /// <summary>取夹具文件的绝对路径。</summary>
    public static string Fixture(string name) => Path.Combine(FixturesDir, name);

    /// <summary>取被测产品源码目录（相对 src/ScreenLeash）。</summary>
    public static string SourceDir(params string[] segments)
    {
        var parts = new[] { RepoRoot, "src", "ScreenLeash" }.Concat(segments).ToArray();
        return Path.Combine(parts);
    }

    private static string Locate(string key)
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"汇编元数据缺少 {key}。请检查 ScreenLeash.Tests.csproj 的 AssemblyAttribute 配置。");
        }

        return Path.GetFullPath(value);
    }
}
