using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SterilizationGenie.Services;

public static class DiagLogger
{
    private static readonly object _sync = new();
    private static string? _logPath;
    public static bool EnableFileLogging { get; set; } = false;

    public static void Init(string? baseDir = null)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(baseDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SterilizationGenie", "Logs")
                : baseDir;
            Directory.CreateDirectory(root);
            _logPath = Path.Combine(root, $"diagnostics-{DateTime.UtcNow:yyyyMMdd}.log");
        }
        catch
        {
            _logPath = null;
        }
    }

    public static void Write(string message)
    {
        try
        {
            var ts = DateTime.Now.ToString("O");
            var line = $"{ts} {message}\n";
            lock (_sync)
            {
                if (EnableFileLogging && !string.IsNullOrWhiteSpace(_logPath))
                {
                    File.AppendAllText(_logPath!, line, Encoding.UTF8);
                }
            }
        }
        catch
        {
            // ignore logging failures
        }
    }
}
