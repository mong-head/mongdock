using System.Diagnostics;
using System.IO;
using System.Text;

namespace Mongdock.Services;

/// <summary>%APPDATA%\mongdock\logs\mongdock.log 로 남기는 간단한 스레드 안전 로그. 1MB 넘으면 .1 로 회전.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogDirectory { get; } =
        Path.Combine(AppInfo.DataDirectory, "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, AppInfo.Name + ".log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(' ').Append(level)
          .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
          .Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        sb.AppendLine();
        string line = sb.ToString();

        Debug.Write(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = LogPath + ".1";
                    File.Delete(old);
                    File.Move(LogPath, old);
                }
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch
            {
                // 로그 실패로 앱이 죽으면 안 됨.
            }
        }
    }
}
