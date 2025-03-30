using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics; 

namespace TarkovTextureConverter.Cli;

class Program
{

    private static readonly CancellationTokenSource _cts = new CancellationTokenSource();

    static async Task<int> Main(string[] args)
    {

        using var loggerFactory = Utils.CreateLoggerFactory();
        var logger = loggerFactory.CreateLogger<Program>();

        Utils.SetupSignalHandlers(logger, _cts);

        var inputArgument = new Argument<DirectoryInfo>(
            name: "input-folder",
            description: "Input folder path containing textures.")
            .ExistingOnly(); 

        var tarkinOption = new Option<bool>(
            aliases: new[] { "--tarkin", "-t" },
            description: "Enable SPECGLOS mode & GLTF update (for Tarkin exporter workflow).");

        var optimizeOption = new Option<bool>(
            aliases: new[] { "--optimize", "-o" },
            description: "Enable higher PNG compression (slower processing, smaller files).");

        var workersOption = new Option<int>(
             aliases: new[] { "--workers", "-w" },

             getDefaultValue: () => Constants.RecommendedWorkers,
             description: $"Number of worker processes to use for image processing.\nDefault: {Constants.RecommendedWorkers} (Based on CPU cores)");

        var rootCommand = new RootCommand("Tarkov Texture Converter: Processes textures and optionally updates GLTF files.")
        {
            inputArgument,
            tarkinOption,
            optimizeOption,
            workersOption
        };

        rootCommand.SetHandler(async (InvocationContext context) =>
        {
            var inputDir = context.ParseResult.GetValueForArgument(inputArgument);
            var tarkinMode = context.ParseResult.GetValueForOption(tarkinOption);
            var optimizePng = context.ParseResult.GetValueForOption(optimizeOption);
            var workers = context.ParseResult.GetValueForOption(workersOption);
            var cancellationToken = context.GetCancellationToken(); 

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

            var cliArgs = new CliArguments(inputDir, tarkinMode, optimizePng, workers);
            await RunCliAsync(cliArgs, loggerFactory, linkedCts.Token);
        });

        try
        {

            return await rootCommand.InvokeAsync(args);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Operation cancelled by user or system signal.");
            return 1; 
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An unhandled exception occurred at the top level.");
            return 1; 
        }
        finally
        {
            logger.LogInformation("Application shutting down.");

             _cts.Dispose(); 
        }
    }

    static async Task RunCliAsync(CliArguments args, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("CliHandler"); 

        logger.LogInformation("==================== Running in Command Line Mode ====================");
        logger.LogInformation("Input Folder: {InputFolder}", args.InputDirectory.FullName);
        logger.LogInformation("SPECGLOS (Tarkin) Mode: {Mode}", args.TarkinMode ? "Enabled" : "Disabled");
        logger.LogInformation("PNG Optimization: {Mode}", args.OptimizePng ? "Enabled" : "Disabled");
        logger.LogInformation("Using {Workers} CPU workers.", args.Workers);

        var overallStopwatch = Stopwatch.StartNew();
        int successful = 0, failed = 0, skipped = 0;
        TextureProcessor? processor = null;

        try
        {

            var progressHandler = new Progress<(int current, int total)>(p =>
            {

                Console.Write($"\rProgress: {p.current}/{p.total} files processed... ");
                 if(p.current == p.total) Console.WriteLine(); 
            });

            processor = new TextureProcessor(args, loggerFactory, cancellationToken);

            (successful, failed, skipped) = await processor.ProcessAllAsync(progressHandler);

            overallStopwatch.Stop();
            string formattedTime = Utils.FormatExecutionTime(overallStopwatch.Elapsed);

            logger.LogInformation("------------------------------------------------------------");
            logger.LogInformation("Processing Summary:");
            logger.LogInformation("  Input Folder:  {InputFolder}", processor.InputFolder);
            logger.LogInformation("  Output Folder: {OutputFolder}", processor.OutputFolder);
            logger.LogInformation("  Successful:    {Successful}", successful);
            logger.LogInformation("  Failed:        {Failed}", failed);
            logger.LogInformation("  Skipped:       {Skipped}", skipped);
            logger.LogInformation("  Total Time:    {FormattedTime}", formattedTime);
            logger.LogInformation("------------------------------------------------------------");

            if (processor.TarkinMode && (successful > 0 || failed > 0 || skipped > 0))
            {
                 if (cancellationToken.IsCancellationRequested)
                 {
                     logger.LogWarning("Skipping GLTF update due to cancellation request.");
                 }
                 else
                 {
                    logger.LogInformation("Tarkin mode enabled, running GLTF update check...");
                    var gltfLogger = loggerFactory.CreateLogger("GltfUtils"); // Keep using specific logger

                    await GltfUtils.UpdateGltfFilesAsync(processor.InputFolder, processor.OutputFolder, gltfLogger, cancellationToken);

                    logger.LogInformation("GLTF update check finished.");
                 }
            }
            else if (processor.TarkinMode)
            {
                 logger.LogInformation("Tarkin mode enabled, but no files were processed. Skipping GLTF update check.");
            }

        }
        catch (OperationCanceledException)
        {

            logger.LogWarning("CLI execution cancelled.");

        }
        catch (DirectoryNotFoundException ex) 
        {
            logger.LogError("Initialization Error: {Message}", ex.Message);

            throw; 
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Critical CLI error during processing.");

            throw; 
        }
        finally
        {
            processor?.Dispose(); 
             overallStopwatch.Stop(); 
        }
    }
}