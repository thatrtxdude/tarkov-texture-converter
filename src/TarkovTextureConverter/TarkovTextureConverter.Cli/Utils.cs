using System.Diagnostics; // For Stopwatch
using Microsoft.Extensions.Logging; // <--- ADD THIS LINE

namespace TarkovTextureConverter.Cli;

public static class Utils
{
    /// <summary>
    /// Inserts a suffix into a filename before its extension.
    /// Ensures the suffix starts with '_' if it doesn't already.
    /// </summary>
    /// <param name="filename">Original filename (e.g., "texture.png")</param>
    /// <param name="suffix">Suffix to insert (e.g., "color")</param>
    /// <returns>Filename with suffix (e.g., "texture_color.png")</returns>
    public static string InsertSuffix(string filename, string suffix)
    {
        if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(suffix))
            return filename;

        string baseName = Path.GetFileNameWithoutExtension(filename);
        string extension = Path.GetExtension(filename); // Includes the dot "."

        // Ensure suffix starts with '_' if needed
        string formattedSuffix = (suffix.StartsWith('_') || string.IsNullOrEmpty(baseName)) ? suffix : $"_{suffix}";

        return $"{baseName}{formattedSuffix}{extension}";
    }

    /// <summary>
    /// Formats a TimeSpan into a human-readable string "Xh Ym Z.ZZs".
    /// </summary>
    public static string FormatExecutionTime(TimeSpan duration)
    {
        return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds:00}.{duration.Milliseconds:000}s";
        // Alternative closer to python output:
        // return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.TotalSeconds % 60:0.00}s";
    }

    /// <summary>
    /// Sets up basic console logging.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false; // Keep it clean like the Python version
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; // Match Python format
                options.SingleLine = true;
            })
            .SetMinimumLevel(LogLevel.Information); // Default to Info
            // Add .AddDebug() for debug output if needed
        });
    }

    /// <summary>
    /// Sets up handlers for Ctrl+C / SIGINT / SIGTERM for graceful shutdown.
    /// </summary>
    public static void SetupSignalHandlers(ILogger logger, CancellationTokenSource cancellationTokenSource)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            logger.LogInformation("Shutdown signal (Ctrl+C) received. Cancelling operations...");
            e.Cancel = true; // Prevent the process from terminating immediately
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException) { } // Ignore if already disposed
        };

        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            // This might run after Main returns, try to cancel one last time
            logger.LogInformation("ProcessExit event received. Ensuring cancellation...");
            try
            {
                 if (!cancellationTokenSource.IsCancellationRequested)
                 {
                    cancellationTokenSource.Cancel();
                 }
            }
            catch (ObjectDisposedException) { } // Ignore if already disposed
             // Give a very short time for logging to flush if needed, but ProcessExit is abrupt
            Task.Delay(100).Wait();
        };
    }
}