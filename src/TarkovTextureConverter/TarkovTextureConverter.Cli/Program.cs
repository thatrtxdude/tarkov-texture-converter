﻿using Microsoft.Extensions.Logging;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics; // For Stopwatch

namespace TarkovTextureConverter.Cli;

class Program
{
    // Cancellation token source for graceful shutdown
    private static readonly CancellationTokenSource _cts = new CancellationTokenSource();

    static async Task<int> Main(string[] args)
    {
        // --- Setup Logging ---
        // Create logger factory early for use everywhere
        using var loggerFactory = Utils.CreateLoggerFactory();
        var logger = loggerFactory.CreateLogger<Program>();

        // --- Setup Graceful Shutdown ---
        Utils.SetupSignalHandlers(logger, _cts);

        // --- Setup Command Line Arguments ---
        var inputArgument = new Argument<DirectoryInfo>(
            name: "input-folder",
            description: "Input folder path containing textures.")
            .ExistingOnly(); // Validate directory exists

        var tarkinOption = new Option<bool>(
            aliases: new[] { "--tarkin", "-t" },
            description: "Enable SPECGLOS mode & GLTF update (for Tarkin exporter workflow).");

        var optimizeOption = new Option<bool>(
            aliases: new[] { "--optimize", "-o" },
            description: "Enable higher PNG compression (slower processing, smaller files).");

        var workersOption = new Option<int>(
             aliases: new[] { "--workers", "-w" },
             // Provide default value factory using Constants.RecommendedWorkers
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
            var cancellationToken = context.GetCancellationToken(); // Use context's token which integrates with ours

             // Combine context token with our global Ctrl+C token
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

            var cliArgs = new CliArguments(inputDir, tarkinMode, optimizePng, workers);
            await RunCliAsync(cliArgs, loggerFactory, linkedCts.Token);
        });

        // --- Execute Command ---
        try
        {
            // Use the InvokeAsync which respects cancellation
            return await rootCommand.InvokeAsync(args);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Operation cancelled by user or system signal.");
            return 1; // Indicate cancellation
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An unhandled exception occurred at the top level.");
            return 1; // Indicate critical failure
        }
        finally
        {
            logger.LogInformation("Application shutting down.");
            // Ensure logging providers flush if necessary (Console usually does)
            // await Task.Delay(100); // Short delay if needed
             _cts.Dispose(); // Dispose our cancellation token source
        }
    }

    // --- CLI Execution Logic ---
    static async Task RunCliAsync(CliArguments args, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("CliHandler"); // Specific logger for this part

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
            // Simple progress reporting to console
            var progressHandler = new Progress<(int current, int total)>(p =>
            {
                // Basic console progress (can be improved with libraries like Spectre.Console)
                Console.Write($"\rProgress: {p.current}/{p.total} files processed... ");
                 if(p.current == p.total) Console.WriteLine(); // New line when done
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

             // Run GLTF update if Tarkin mode was enabled and processing happened
             // Only run if there were successful conversions, implying output folder might be relevant
            if (processor.TarkinMode && (successful > 0 || failed > 0 || skipped > 0)) // Run even if only skipped/failed? Check python logic if needed. Let's run if any processing attempted.
            {
                 if (cancellationToken.IsCancellationRequested)
                 {
                     logger.LogWarning("Skipping GLTF update due to cancellation request.");
                 }
                 else
                 {
                    logger.LogInformation("Tarkin mode enabled, running GLTF update check...");
                    var gltfLogger = loggerFactory.CreateLogger("GltfUtils");
                    // Run synchronous GLTF update on a background thread if it might be long
                    await Task.Run(() => GltfUtils.UpdateGltfFiles(processor.InputFolder, processor.OutputFolder, gltfLogger), cancellationToken);
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
            // Already logged by the cancellation setup or ProcessAllAsync
            logger.LogWarning("CLI execution cancelled.");
            // Optionally report partial results if available from processor state
        }
        catch (DirectoryNotFoundException ex) // From Processor constructor
        {
            logger.LogError("Initialization Error: {Message}", ex.Message);
            // Set exit code in Main
            throw; // Re-throw to be caught by Main's top-level handler
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Critical CLI error during processing.");
            // Set exit code in Main
            throw; // Re-throw to be caught by Main's top-level handler
        }
        finally
        {
            processor?.Dispose(); // Dispose processor if it was created
             overallStopwatch.Stop(); // Ensure stopped
        }
    }
}