using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TianBai_Launcher;

/// <summary>
/// 启动器自身配置服务。
/// 这些配置只属于 WinUI 启动器，不写入 Unity StreamingAssets，避免和游戏运行配置混在一起。
/// </summary>
public static class LauncherSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TianBaiLauncher");

    public static string SettingsPath => Path.Combine(DataDirectory, "launcher_settings.json");

    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    public static string DefaultUnityLogPath => Path.Combine(LogsDirectory, "unity_player.log");

    public static LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                LauncherSettings created = LauncherSettings.CreateDefault();
                Save(created);
                return created;
            }

            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            LauncherSettings? settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
            settings ??= LauncherSettings.CreateDefault();
            settings.EnsureDefaults();
            return settings;
        }
        catch
        {
            return LauncherSettings.CreateDefault();
        }
    }

    public static void Save(LauncherSettings settings)
    {
        settings.EnsureDefaults();
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
    }
}

/// <summary>
/// 启动器自身配置模型。
/// 后续启动器设置页继续扩展时，可以在这里追加字段并保持向后兼容。
/// </summary>
public sealed class LauncherSettings
{
    public string GameExecutablePath { get; set; } = "";
    public string ExtraLaunchArguments { get; set; } = "";
    public int LogLineCount { get; set; } = 120;
    public bool ShowLogOnMainPage { get; set; }
    public string UnityLogFilePath { get; set; } = "";

    public static LauncherSettings CreateDefault()
    {
        return new LauncherSettings
        {
            LogLineCount = 120,
            ShowLogOnMainPage = false,
            UnityLogFilePath = LauncherSettingsService.DefaultUnityLogPath
        };
    }

    public void EnsureDefaults()
    {
        if (LogLineCount < 10)
        {
            LogLineCount = 120;
        }

        if (string.IsNullOrWhiteSpace(UnityLogFilePath))
        {
            UnityLogFilePath = LauncherSettingsService.DefaultUnityLogPath;
        }
    }
}
