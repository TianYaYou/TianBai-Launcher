using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TianBai_Launcher;

/// <summary>
/// 天白 Unity 程序启动服务。
/// UI 只负责展示和按钮事件，路径查找、校验、Process 启动都集中放在这里。
/// </summary>
public static class TianBaiLaunchService
{
    private const string UnityExeName = "Tian Bai Aic.exe";
    private const string UnityExeNameNoSpace = "TianBaiAic.exe";
    private const string EnvGamePath = "TIANBAI_GAME_EXE_PATH";

    /// <summary>
    /// 自动寻找 Unity 构建产物。
    /// 优先环境变量，之后从启动器输出目录逐级向上寻找大仓库 Release 目录。
    /// </summary>
    public static string? FindDefaultExecutable()
    {
        string? envPath = Environment.GetEnvironmentVariable(EnvGamePath);
        if (IsExecutableFile(envPath))
        {
            return Path.GetFullPath(envPath!);
        }

        foreach (string root in GetSearchRoots())
        {
            foreach (string candidate in BuildCandidates(root))
            {
                if (IsExecutableFile(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 启动 Unity 天白程序。
    /// WorkingDirectory 必须设置到 exe 所在目录，否则 Unity 构建可能找不到 Data 文件夹。
    /// </summary>
    public static TianBaiLaunchResult Launch(string executablePath)
    {
        return Launch(new TianBaiLaunchOptions { ExecutablePath = executablePath });
    }

    /// <summary>
    /// 启动 Unity 天白程序，并允许启动器附加启动参数和日志文件路径。
    /// </summary>
    public static TianBaiLaunchResult Launch(TianBaiLaunchOptions options)
    {
        string executablePath = options.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return TianBaiLaunchResult.Fail("请先填写 Tian Bai Aic.exe 的路径。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath.Trim('"'));
        }
        catch (Exception e)
        {
            return TianBaiLaunchResult.Fail($"路径格式不正确：{e.Message}");
        }

        if (!IsExecutableFile(fullPath))
        {
            return TianBaiLaunchResult.Fail($"没有找到可执行文件：{fullPath}");
        }

        string? workingDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return TianBaiLaunchResult.Fail("无法解析程序所在目录。");
        }

        try
        {
            EnsureRuntimeStreamingAssets(fullPath, options.SourceStreamingAssetsPath);
            string arguments = BuildArguments(options);
            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = workingDirectory,
                Arguments = arguments,
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return TianBaiLaunchResult.Fail("启动进程失败：Process.Start 没有返回进程对象。");
            }

            return TianBaiLaunchResult.Ok($"已启动：{fullPath}", process);
        }
        catch (Exception e)
        {
            return TianBaiLaunchResult.Fail($"启动进程失败：{e.Message}");
        }
    }

    private static void EnsureRuntimeStreamingAssets(string executablePath, string sourceStreamingAssetsPath)
    {
        if (string.IsNullOrWhiteSpace(sourceStreamingAssetsPath) || !Directory.Exists(sourceStreamingAssetsPath))
        {
            return;
        }

        string? executableDirectory = Path.GetDirectoryName(executablePath);
        string executableName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory) || string.IsNullOrWhiteSpace(executableName))
        {
            return;
        }

        string targetStreamingAssets = Path.Combine(executableDirectory, $"{executableName}_Data", "StreamingAssets");
        if (!Directory.Exists(targetStreamingAssets))
        {
            return;
        }

        // 本地 TTS 模型很大，Unity 构建或手动 Release 目录容易漏拷。
        // 启动前检查关键文件，缺失时从项目 StreamingAssets 同步过去，避免 Unity 运行时找不到 sherpa-onnx。
        EnsureDirectoryCopiedIfMissingCriticalFiles(
            Path.Combine(sourceStreamingAssetsPath, "sherpa-onnx"),
            Path.Combine(targetStreamingAssets, "sherpa-onnx"),
            new[]
            {
                Path.Combine("models", "speech-synthesis", "vits-melo-tts-zh_en", "model.onnx"),
                Path.Combine("models", "speech-synthesis", "vits-melo-tts-zh_en", "tokens.txt")
            });
    }

    private static void EnsureDirectoryCopiedIfMissingCriticalFiles(string sourceDirectory, string targetDirectory, IReadOnlyList<string> criticalRelativeFiles)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        bool missingCriticalFile = criticalRelativeFiles.Any(relativePath => !File.Exists(Path.Combine(targetDirectory, relativePath)));
        if (!missingCriticalFile)
        {
            return;
        }

        CopyDirectory(sourceDirectory, targetDirectory);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            string targetFile = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
            if (File.Exists(targetFile)
                && File.GetLastWriteTimeUtc(targetFile) == File.GetLastWriteTimeUtc(sourceFile)
                && new FileInfo(targetFile).Length == new FileInfo(sourceFile).Length)
            {
                continue;
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
            File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(sourceFile));
        }

        foreach (string sourceChildDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(sourceChildDirectory, Path.Combine(targetDirectory, Path.GetFileName(sourceChildDirectory)));
        }
    }

    private static string BuildArguments(TianBaiLaunchOptions options)
    {
        string arguments = options.ExtraArguments?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(options.LogFilePath)
            && arguments.IndexOf("-logFile", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogFilePath)!);
            File.WriteAllText(options.LogFilePath, string.Empty);
            string logArgument = $"-logFile \"{options.LogFilePath}\"";
            arguments = string.IsNullOrWhiteSpace(arguments) ? logArgument : $"{arguments} {logArgument}";
        }

        return arguments;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static IEnumerable<string> BuildCandidates(string startPath)
    {
        DirectoryInfo? current = new DirectoryInfo(startPath);
        while (current != null)
        {
            // 开发仓库结构：大仓库/Release/Tian Bai Aic.exe
            yield return Path.Combine(current.FullName, "Release", UnityExeName);
            yield return Path.Combine(current.FullName, "Release", UnityExeNameNoSpace);

            // 兼容直接从构建目录旁边启动。
            yield return Path.Combine(current.FullName, UnityExeName);
            yield return Path.Combine(current.FullName, UnityExeNameNoSpace);

            current = current.Parent;
        }
    }

    private static bool IsExecutableFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && File.Exists(path)
               && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class TianBaiLaunchOptions
{
    public string ExecutablePath { get; init; } = "";
    public string ExtraArguments { get; init; } = "";
    public string LogFilePath { get; init; } = "";
    public string SourceStreamingAssetsPath { get; init; } = "";
}

public sealed class TianBaiLaunchResult
{
    public bool Success { get; private init; }
    public string Message { get; private init; } = "";
    public Process? Process { get; private init; }

    public static TianBaiLaunchResult Ok(string message, Process process)
    {
        return new TianBaiLaunchResult { Success = true, Message = message, Process = process };
    }

    public static TianBaiLaunchResult Fail(string message)
    {
        return new TianBaiLaunchResult { Success = false, Message = message };
    }
}
