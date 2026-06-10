using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TianBai_Launcher;

/// <summary>
/// 启动器配置文件服务。
/// 这里集中处理“大仓库路径查找、StreamingAssets 定位、JSON/text 读写”，避免 UI 代码到处拼路径。
/// </summary>
public static class LauncherConfigService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 自动定位 Unity 的 StreamingAssets 目录。
    /// 优先使用打包后的 xxx_Data/StreamingAssets；找不到时再回到开发期 TianBaiAic/Assets/StreamingAssets。
    /// </summary>
    public static LauncherConfigPaths FindPaths(string? gameExecutablePath = null)
    {
        // 如果启动器已经保存了游戏 exe 路径，最可靠的方式是直接从 exe 名推导 Unity Data 目录。
        if (TryFindPackagedStreamingAssetsFromExecutable(gameExecutablePath, out string? executableStreamingAssets))
        {
            return LauncherConfigPaths.FromStreamingAssets(executableStreamingAssets);
        }

        // 首次打开启动器时可能还没有保存 exe 路径，但启动服务已经知道如何自动寻找 Release 里的游戏。
        if (TryFindPackagedStreamingAssetsFromExecutable(TianBaiLaunchService.FindDefaultExecutable(), out string? defaultExecutableStreamingAssets))
        {
            return LauncherConfigPaths.FromStreamingAssets(defaultExecutableStreamingAssets);
        }

        foreach (string root in GetSearchRoots())
        {
            DirectoryInfo? current = new(root);
            while (current != null)
            {
                if (TryFindPackagedStreamingAssetsNear(current.FullName, out string? packagedStreamingAssets))
                {
                    return LauncherConfigPaths.FromStreamingAssets(packagedStreamingAssets);
                }

                current = current.Parent;
            }
        }

        foreach (string root in GetSearchRoots())
        {
            DirectoryInfo? current = new(root);
            while (current != null)
            {
                string developmentStreamingAssets = Path.Combine(current.FullName, "TianBaiAic", "Assets", "StreamingAssets");
                if (IsUsableStreamingAssets(developmentStreamingAssets))
                {
                    return LauncherConfigPaths.FromStreamingAssets(developmentStreamingAssets);
                }

                current = current.Parent;
            }
        }

        // 找不到时仍然给一个基于当前目录的默认值，UI 可以显示明确错误，而不是空引用崩掉。
        string fallback = Path.Combine(Directory.GetCurrentDirectory(), "TianBaiAic", "Assets", "StreamingAssets");
        return LauncherConfigPaths.FromStreamingAssets(fallback);
    }

    public static string ReadTextOrEmpty(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
    }

    public static void WriteText(string path, string content)
    {
        EnsureParentDirectory(path);
        File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static bool TryReadJsonObject(string path, out JsonObject json, out string? error)
    {
        json = new JsonObject();
        error = null;

        if (!File.Exists(path))
        {
            error = $"配置文件不存在：{path}";
            return false;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(ReadTextOrEmpty(path));
            if (node is not JsonObject obj)
            {
                error = $"配置文件不是 JSON 对象：{path}";
                return false;
            }

            json = obj;
            return true;
        }
        catch (Exception e)
        {
            error = $"JSON 解析失败：{e.Message}";
            return false;
        }
    }

    public static void WriteJsonObject(string path, JsonObject json)
    {
        EnsureParentDirectory(path);
        File.WriteAllText(path, json.ToJsonString(WriteOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string GetString(JsonObject json, string name, string fallback = "")
    {
        return json.TryGetPropertyValue(name, out JsonNode? node) ? node?.GetValue<string>() ?? fallback : fallback;
    }

    public static bool GetBool(JsonObject json, string name, bool fallback = false)
    {
        return json.TryGetPropertyValue(name, out JsonNode? node) && node != null ? node.GetValue<bool>() : fallback;
    }

    public static int GetInt(JsonObject json, string name, int fallback = 0)
    {
        return json.TryGetPropertyValue(name, out JsonNode? node) && node != null ? node.GetValue<int>() : fallback;
    }

    public static double GetDouble(JsonObject json, string name, double fallback = 0)
    {
        return json.TryGetPropertyValue(name, out JsonNode? node) && node != null ? node.GetValue<double>() : fallback;
    }

    public static string ExtractStringFromPossiblyBrokenJson(string rawJson, string name, string fallback = "")
    {
        Match match = Regex.Match(rawJson, $"\\\"{Regex.Escape(name)}\\\"\\s*:\\s*\\\"(?<value>.*?)\\\"", RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value : fallback;
    }

    public static bool ExtractBoolFromPossiblyBrokenJson(string rawJson, string name, bool fallback = false)
    {
        Match match = Regex.Match(rawJson, $"\\\"{Regex.Escape(name)}\\\"\\s*:\\s*(?<value>true|false)", RegexOptions.IgnoreCase);
        return match.Success ? bool.Parse(match.Groups["value"].Value) : fallback;
    }

    public static int ExtractIntFromPossiblyBrokenJson(string rawJson, string name, int fallback = 0)
    {
        Match match = Regex.Match(rawJson, $"\\\"{Regex.Escape(name)}\\\"\\s*:\\s*(?<value>-?\\d+)");
        return match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    public static double ExtractDoubleFromPossiblyBrokenJson(string rawJson, string name, double fallback = 0)
    {
        Match match = Regex.Match(rawJson, $"\\\"{Regex.Escape(name)}\\\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)");
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
    }

    public static string[] ExtractStringArrayFromPossiblyBrokenJson(string rawJson, string name, string[] fallback)
    {
        Match match = Regex.Match(rawJson, $"\\\"{Regex.Escape(name)}\\\"\\s*:\\s*\\[(?<items>.*?)\\]", RegexOptions.Singleline);
        if (!match.Success) return fallback;

        List<string> values = new();
        foreach (Match item in Regex.Matches(match.Groups["items"].Value, "\\\"(?<value>.*?)\\\""))
        {
            values.Add(item.Groups["value"].Value);
        }

        return values.Count > 0 ? values.ToArray() : fallback;
    }

    public static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static string ResolveStreamingAssetsPath(LauncherConfigPaths paths, string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath)) return string.Empty;
        return Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(paths.StreamingAssetsPath, relativeOrAbsolutePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static bool TryFindPackagedStreamingAssetsFromExecutable(string? executablePath, out string streamingAssets)
    {
        streamingAssets = string.Empty;
        string normalizedExecutablePath = executablePath?.Trim().Trim('"') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedExecutablePath) || !File.Exists(normalizedExecutablePath))
        {
            return false;
        }

        string? executableDirectory = Path.GetDirectoryName(Path.GetFullPath(normalizedExecutablePath));
        string executableName = Path.GetFileNameWithoutExtension(normalizedExecutablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory) || string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        string candidate = Path.Combine(executableDirectory, $"{executableName}_Data", "StreamingAssets");
        if (!IsUsableStreamingAssets(candidate))
        {
            return false;
        }

        streamingAssets = candidate;
        return true;
    }

    private static bool TryFindPackagedStreamingAssetsNear(string directory, out string streamingAssets)
    {
        streamingAssets = string.Empty;

        // 常见打包名优先判断，避免被其他 Unity Data 目录误命中。
        string[] preferredCandidates =
        {
            Path.Combine(directory, "Tian Bai Aic_Data", "StreamingAssets"),
            Path.Combine(directory, "TianBaiAic_Data", "StreamingAssets")
        };

        foreach (string candidate in preferredCandidates)
        {
            if (IsUsableStreamingAssets(candidate))
            {
                streamingAssets = candidate;
                return true;
            }
        }

        if (!Directory.Exists(directory))
        {
            return false;
        }

        // 兼容后续改 exe 名的情况：Unity 打包目录通常是 “游戏名_Data/StreamingAssets”。
        foreach (string dataDirectory in Directory.EnumerateDirectories(directory, "*_Data"))
        {
            string candidate = Path.Combine(dataDirectory, "StreamingAssets");
            if (IsUsableStreamingAssets(candidate))
            {
                streamingAssets = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsUsableStreamingAssets(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        // 这里不要求所有目录都存在，避免早期包缺资源时完全无法打开设置页。
        // 但至少要看到一个天白自己的资源目录，防止误选其他 Unity 项目的 StreamingAssets。
        string[] knownChildren = { "AI", "Memory", "Whisper", "sherpa-onnx" };
        foreach (string child in knownChildren)
        {
            if (Directory.Exists(Path.Combine(path, child)))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureParentDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}

/// <summary>
/// 启动器当前需要编辑的配置路径集合。
/// 后续如果启动器改为用户数据目录，只需要替换这里的路径来源。
/// </summary>
public sealed class LauncherConfigPaths
{
    public required string StreamingAssetsPath { get; init; }
    public required string AiConfigPath { get; init; }
    public required string TtsConfigPath { get; init; }
    public required string WhisperConfigPath { get; init; }
    public required string DialoguePromptPath { get; init; }
    public required string ControlPlannerPromptPath { get; init; }
    public required string MemoryDirectory { get; init; }
    public required string FavorabilityPath { get; init; }
    public required string MemoryJsonlPath { get; init; }

    public static LauncherConfigPaths FromStreamingAssets(string streamingAssetsPath)
    {
        string ai = Path.Combine(streamingAssetsPath, "AI");
        string memory = Path.Combine(streamingAssetsPath, "Memory");

        return new LauncherConfigPaths
        {
            StreamingAssetsPath = streamingAssetsPath,
            AiConfigPath = Path.Combine(ai, "ai_config.json"),
            TtsConfigPath = Path.Combine(ai, "tts_config.json"),
            WhisperConfigPath = Path.Combine(ai, "whisper_config.json"),
            DialoguePromptPath = Path.Combine(ai, "dialogue_prompt.txt"),
            ControlPlannerPromptPath = Path.Combine(ai, "control_planner_prompt.txt"),
            MemoryDirectory = memory,
            FavorabilityPath = Path.Combine(memory, "favorability.json"),
            MemoryJsonlPath = Path.Combine(memory, "memory.jsonl")
        };
    }
}
