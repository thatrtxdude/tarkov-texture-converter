using Microsoft.Extensions.Logging; 

namespace TarkovTextureConverter.Cli;

public static class Utils
{
    public static string InsertSuffix(string filename, string suffix)
    {
        if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(suffix))
            return filename;

        string baseName = Path.GetFileNameWithoutExtension(filename);
        string extension = Path.GetExtension(filename);

        string formattedSuffix = (suffix.StartsWith('_') || string.IsNullOrEmpty(baseName)) ? suffix : $"_{suffix}";

        return $"{baseName}{formattedSuffix}{extension}";
    }

    public static string FormatExecutionTime(TimeSpan duration)
    {
        return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds:00}.{duration.Milliseconds:000}s";
    }

    public static ILoggerFactory CreateLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; 
                options.SingleLine = true;
            })
            .SetMinimumLevel(LogLevel.Information); 
            
        });
    }
    public static void SetupSignalHandlers(ILogger logger, CancellationTokenSource cancellationTokenSource)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            logger.LogInformation("Shutdown signal (Ctrl+C) received. Cancelling operations...");
            e.Cancel = true;
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException) { }
        };

        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            logger.LogInformation("ProcessExit event received. Ensuring cancellation...");
            try
            {
                 if (!cancellationTokenSource.IsCancellationRequested)
                 {
                    cancellationTokenSource.Cancel();
                 }
            }
            catch (ObjectDisposedException) { }
            Task.Delay(100).Wait();
        };
    }
}