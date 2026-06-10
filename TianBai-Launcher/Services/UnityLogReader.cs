using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TianBai_Launcher;

/// <summary>
/// Unity 日志读取工具。
/// 当前先用文件 tail 方案：启动器指定 Unity 的 -logFile 路径，然后这里只读取最后 N 行用于 UI 展示。
/// </summary>
public static class UnityLogReader
{
    public static string ReadLastLines(string logPath, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return "Unity 日志路径未设置。";
        }

        if (!File.Exists(logPath))
        {
            return $"等待 Unity 写入日志：{logPath}";
        }

        try
        {
            Queue<string> lines = new(Math.Max(1, lineCount));
            using FileStream stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                while (lines.Count > lineCount)
                {
                    lines.Dequeue();
                }
            }

            return lines.Count == 0 ? "(日志文件为空)" : string.Join(Environment.NewLine, lines);
        }
        catch (IOException)
        {
            // Unity 正在写日志时偶尔会锁文件；下一次刷新会自动恢复。
            return "Unity 日志正在写入，稍后刷新。";
        }
        catch (UnauthorizedAccessException)
        {
            return $"没有权限读取 Unity 日志：{logPath}";
        }
    }
}
