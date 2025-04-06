using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace TarkovTextureConverter.Cli;

public class TextureProcessor : IDisposable
{
    private readonly ILogger<TextureProcessor> _logger;
    private readonly CliArguments _args;
    private readonly int _pngCompression;
    private readonly CancellationToken _cancellationToken;

    public string InputFolder { get; }
    public string OutputFolder { get; }
    public int MaxWorkers { get; }
    public bool TarkinMode => _args.TarkinMode;

    public TextureProcessor(CliArguments args, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        _args = args;
        _logger = loggerFactory.CreateLogger<TextureProcessor>();
        _cancellationToken = cancellationToken;

        InputFolder = args.InputDirectory.FullName;
        if (!Directory.Exists(InputFolder))
        {
            throw new DirectoryNotFoundException($"Input folder does not exist: {InputFolder}");
        }

        MaxWorkers = args.Workers > 0 ? args.Workers : Constants.RecommendedWorkers;
        _pngCompression = args.OptimizePng ? Constants.PngCompressionOptimized : Constants.PngCompressionDefault;

        OutputFolder = GetUniqueOutputFolder(InputFolder);

        _logger.LogInformation("Initialized TextureProcessor:");
        _logger.LogInformation($"  Input Folder: {InputFolder}");
        _logger.LogInformation($"  Output Folder: {OutputFolder}");
        _logger.LogInformation($"  CPU Workers: {MaxWorkers}");
        _logger.LogInformation($"  PNG Optimization: {args.OptimizePng} (Compression Level: {_pngCompression})");
        _logger.LogInformation($"  SPECGLOS (Tarkin) Mode: {TarkinMode}");
    }

    private string GetUniqueOutputFolder(string inputPath)
    {
        string baseOutput = Path.Combine(inputPath, Constants.DefaultOutputSubfolder);
        string outputFolder = baseOutput;
        int counter = 1;
        while (Directory.Exists(outputFolder) || File.Exists(outputFolder))
        {
            outputFolder = $"{baseOutput}_{counter}";
            counter++;
            if (counter > 1000)
            {
                throw new IOException($"Could not find a unique output folder name near {baseOutput} after {counter} attempts.");
            }
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
            return outputFolder;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create output directory {OutputFolder}", outputFolder);
            throw;
        }
    }

    public static TextureType? GetTextureType(string filename, bool tarkinMode)
    {
        string baseName = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        string[] parts = baseName.Split('_');

        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string part = parts[i];
            switch (part)
            {
                case "n":
                case "normal":
                case "nrm":
                    return TextureType.Normal;
                case "d":
                case "diff":
                case "diffuse":
                case "albedo":
                    return TextureType.Diffuse;
                case "g":
                case "gloss":
                case "gls":
                    return tarkinMode ? null : TextureType.Gloss;
                case "sg":
                case "specglos":
                    return tarkinMode ? TextureType.SpecGlos : null;
            }
        }

        return tarkinMode ? null : TextureType.Normal;
    }

    private static Mat? LoadImage(string inputPath, ILogger logger)
    {
        try
        {
            Mat img = Cv2.ImRead(inputPath, ImreadModes.Unchanged);

            if (img == null || img.Empty())
            {
                logger.LogError("Failed to load image (OpenCV returned null/empty): {InputPath}", inputPath);
                return null;
            }

            Mat rgbaImg;
            int channels = img.Channels();

            if (channels == 1)
            {
                rgbaImg = new Mat();
                Cv2.CvtColor(img, rgbaImg, ColorConversionCodes.GRAY2BGRA);
                img.Dispose();
            }
            else if (channels == 3)
            {
                rgbaImg = new Mat();
                Cv2.CvtColor(img, rgbaImg, ColorConversionCodes.BGR2BGRA);
                img.Dispose();
            }
            else if (channels == 4)
            {
                rgbaImg = img;
            }
            else
            {
                logger.LogError("Unsupported number of channels ({Channels}) in image: {InputPath}", channels, inputPath);
                img.Dispose();
                return null;
            }

            return rgbaImg;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading image {InputPath} with OpenCV", inputPath);
            return null;
        }
    }

    private static Mat ProcessNormalMapStandard(Mat imgArray, ILogger logger)
    {
        if (imgArray.Channels() != 4)
        {
            logger.LogWarning("Standard normal map processing expects 4 channels (BGRA) but got {Channels}. Attempting conversion.", imgArray.Channels());
            return imgArray.Clone();
        }

        Mat processed = new Mat(imgArray.Rows, imgArray.Cols, MatType.CV_8UC4);
        Mat[] channels = Cv2.Split(imgArray);
        try
        {
            Mat[] outputChannels = new Mat[4];
            outputChannels[0] = channels[3];
            outputChannels[1] = channels[1];
            outputChannels[2] = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255));
            outputChannels[3] = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255));

            Cv2.Merge(outputChannels, processed);

            outputChannels[2].Dispose();
            outputChannels[3].Dispose();
        }
        finally
        {
            foreach(var ch in channels) ch.Dispose();
        }

        return processed;
    }

    private static Mat ProcessNormalMapTarkin(Mat imgArray, ILogger logger)
    {
        Mat processed = imgArray.Clone();
        if (processed.Channels() == 4)
        {
            Mat[] channels = Cv2.Split(processed);
            try
            {
                channels[3].SetTo(Scalar.All(255));
                Cv2.Merge(channels, processed);
            }
            finally
            {
                foreach(var ch in channels) ch.Dispose();
            }
        }
        else if (processed.Channels() == 3)
        {
            logger.LogWarning("Tarkin normal map processing received 3 channels, converting to BGRA.");
            Mat tempRgba = new Mat();
            Cv2.CvtColor(processed, tempRgba, ColorConversionCodes.BGR2BGRA);
            processed.Dispose();
            processed = tempRgba;

            Mat[] channels = Cv2.Split(processed);
            try
            {
                channels[3].SetTo(Scalar.All(255));
                Cv2.Merge(channels, processed);
            }
            finally
            {
                foreach(var ch in channels) ch.Dispose();
            }
        }
        return processed;
    }

    private static (Mat color, Mat? alpha) ProcessDiffuseMapStandard(Mat imgArray, ILogger logger)
    {
        if (imgArray.Channels() != 4)
        {
            logger.LogError("Diffuse map standard processing expects 4 channels (BGRA), got {Channels}.", imgArray.Channels());
            return (imgArray.Clone(), null);
        }

        Mat colorOnly = new Mat();
        Mat? alphaMap = null;
        Mat[] channels = Cv2.Split(imgArray);

        try
        {
            using (Mat solidAlpha = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
            {
                Mat[] colorChannels = { channels[0], channels[1], channels[2], solidAlpha };
                Cv2.Merge(colorChannels, colorOnly);
            }

            Cv2.MinMaxLoc(channels[3], out double minVal, out double maxVal);

            if (minVal < 255)
            {
                alphaMap = new Mat();
                using (Mat solidAlpha = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
                {
                    Mat[] alphaChannels = { channels[3], channels[3], channels[3], solidAlpha };
                    Cv2.Merge(alphaChannels, alphaMap);
                }
            }
        }
        finally
        {
            foreach (var ch in channels) ch.Dispose();
        }

        return (colorOnly, alphaMap);
    }

    private static Mat ProcessDiffuseMapTarkin(Mat imgArray, ILogger logger)
    {
        Mat colorOnly = new Mat();
        if (imgArray.Channels() == 4)
        {
            Mat[] channels = Cv2.Split(imgArray);
            try
            {
                using (Mat solidAlpha = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
                {
                    Mat[] colorChannels = { channels[0], channels[1], channels[2], solidAlpha };
                    Cv2.Merge(colorChannels, colorOnly);
                }
            }
            finally
            {
                foreach(var ch in channels) ch.Dispose();
            }
        }
        else if (imgArray.Channels() == 3)
        {
            logger.LogWarning("Tarkin diffuse processing received 3 channels, converting to BGRA.");
            Cv2.CvtColor(imgArray, colorOnly, ColorConversionCodes.BGR2BGRA);
        }
        else
        {
            logger.LogError("Cannot process Tarkin diffuse map with {Channels} channels.", imgArray.Channels());
            return imgArray.Clone();
        }
        return colorOnly;
    }

    private static Mat ProcessGlossMap(Mat imgArray, ILogger logger)
    {
        Mat sourceMat = imgArray;
        bool sourceNeedsDispose = false;
        if (imgArray.Channels() == 3)
        {
            logger.LogWarning("Gloss map processing received 3 channels, converting to BGRA.");
            sourceMat = new Mat();
            Cv2.CvtColor(imgArray, sourceMat, ColorConversionCodes.BGR2BGRA);
            sourceNeedsDispose = true;
        }
        else if (imgArray.Channels() != 4)
        {
            logger.LogError("Gloss map processing requires 3 or 4 channels, got {Channels}.", imgArray.Channels());
            return imgArray.Clone();
        }

        Mat roughnessMap = new Mat();
        try
        {
             Mat[] channels = Cv2.Split(sourceMat);
             try
             {
                 using Mat bgr = new Mat(), inverted = new Mat();
                 Cv2.Merge(new[] { channels[0], channels[1], channels[2] }, bgr);
                 Cv2.BitwiseNot(bgr, inverted);
                 Mat[] invertedChannels = Cv2.Split(inverted);
                 try
                 {
                    Mat roughnessChannel = invertedChannels[2];

                    using (Mat solidAlpha = new Mat(sourceMat.Size(), MatType.CV_8UC1, Scalar.All(255)))
                    {
                        Mat[] roughnessOutputChannels = { roughnessChannel, roughnessChannel, roughnessChannel, solidAlpha };
                        Cv2.Merge(roughnessOutputChannels, roughnessMap);
                    }
                 }
                 finally
                 {
                     foreach(var ch in invertedChannels) ch.Dispose();
                 }
             }
             finally
             {
                 foreach(var ch in channels) ch.Dispose();
             }
        }
        finally
        {
            if (sourceNeedsDispose) sourceMat.Dispose();
        }

        return roughnessMap;
    }

    private static (Mat specular, Mat roughness) ProcessSpecGlosMapSplit(Mat imgArray, ILogger logger)
    {
        if (imgArray.Channels() != 4)
        {
            logger.LogError("SPECGLOS map processing requires 4 channels (BGRA), got {Channels}.", imgArray.Channels());
             return (imgArray.Clone(), imgArray.Clone());
        }

        Mat specularMap = new Mat();
        Mat roughnessMap = new Mat();
        Mat[] channels = Cv2.Split(imgArray);

        try
        {
            using (Mat solidAlphaSpec = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
            {
                Mat[] specChannels = { channels[0], channels[1], channels[2], solidAlphaSpec };
                Cv2.Merge(specChannels, specularMap);
            }

            using (Mat invertedAlpha = new Mat())
            {
                Cv2.BitwiseNot(channels[3], invertedAlpha);
                using (Mat solidAlphaRough = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
                {
                     Mat[] roughChannels = { invertedAlpha, invertedAlpha, invertedAlpha, solidAlphaRough };
                     Cv2.Merge(roughChannels, roughnessMap);
                }
            }
        }
        finally
        {
            foreach (var ch in channels) ch.Dispose();
        }

        return (specularMap, roughnessMap);
    }


    private async Task SaveImageAsync(Mat imageArray, string outputPath, int compression, ILogger logger, CancellationToken cancellationToken)
    {
        string outputDir = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException("Invalid output path");
        string baseName = Path.GetFileNameWithoutExtension(outputPath);
        string finalPath = Path.Combine(outputDir, baseName + ".png");

        try
        {
            Directory.CreateDirectory(outputDir);

            if (imageArray.Channels() != 3 && imageArray.Channels() != 4)
            {
                throw new ArgumentException($"Unsupported channel count for saving PNG: {imageArray.Channels()}");
            }

            int[] compressionParams = { (int)ImwriteFlags.PngCompression, compression };
            byte[] buffer;

            buffer = await Task.Run(() => {
                cancellationToken.ThrowIfCancellationRequested();
                
                int[] compressionParams = [ 
                    (int)ImwriteFlags.PngCompression, compression,
                    (int)ImwriteFlags.PngStrategy, 3,  // Use optimal filtering
                    (int)ImwriteFlags.PngBilevel, 0 // No bilevel encoding
                ];
                
                bool success = Cv2.ImEncode(".png", imageArray, out buffer, compressionParams);
                if (!success || buffer == null || buffer.Length == 0)
                {
                    throw new IOException($"OpenCV ImEncode failed for {finalPath}.");
                }
                return buffer;
            }, cancellationToken);


            await using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await fs.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Save operation cancelled for {FinalPath}", finalPath);
            try { if(File.Exists(finalPath)) File.Delete(finalPath); } catch { /* Ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save image to {FinalPath}", finalPath);
            throw new IOException($"Failed to save image to {finalPath}: {ex.Message}", ex);
        }
    }

    public FileProcessResult ProcessTextureReturnData(string inputPath)
    {
        string filename = Path.GetFileName(inputPath);
        string baseName = Path.GetFileNameWithoutExtension(filename);

        Mat? imgArray = null;
        try
        {
            imgArray = LoadImage(inputPath, _logger);
            if (imgArray == null || imgArray.Empty())
            {
                return new FileProcessResult(ProcessStatus.Failed, filename, Message: $"Failed to load image");
            }

            TextureType? textureType = GetTextureType(filename, TarkinMode);
            if (textureType == null)
            {
                 imgArray?.Dispose();
                return new FileProcessResult(ProcessStatus.Skipped, filename, Message: $"Skipped (Suffix/Mode combination)");
            }

            var processedDataDict = new Dictionary<string, Mat>();
            bool processSuccess = true;

            try
            {
                switch (textureType.Value)
                {
                    case TextureType.Diffuse:
                        if (TarkinMode)
                        {
                            processedDataDict["color"] = ProcessDiffuseMapTarkin(imgArray, _logger);
                        }
                        else
                        {
                            var (color, alpha) = ProcessDiffuseMapStandard(imgArray, _logger);
                            processedDataDict["color"] = color;
                            if (alpha != null) processedDataDict["alpha"] = alpha;
                        }
                        break;

                    case TextureType.SpecGlos:
                        var (spec, rough) = ProcessSpecGlosMapSplit(imgArray, _logger);
                        processedDataDict["spec"] = spec;
                        processedDataDict["roughness"] = rough;
                        break;

                    case TextureType.Normal:
                        processedDataDict["converted"] = TarkinMode
                            ? ProcessNormalMapTarkin(imgArray, _logger)
                            : ProcessNormalMapStandard(imgArray, _logger);
                        break;

                    case TextureType.Gloss:
                        processedDataDict["roughness"] = ProcessGlossMap(imgArray, _logger);
                        break;

                    default:
                        _logger.LogError("Internal logic error: Unknown texture type '{TextureType}' for file {Filename}", textureType.Value, filename);
                         processSuccess = false;
                        break;
                }
            }
            catch(Exception procEx)
            {
                 _logger.LogError(procEx, "Error during image processing logic for {Filename}", filename);
                 processSuccess = false;
                 foreach (var mat in processedDataDict.Values) mat.Dispose();
                 processedDataDict.Clear();
            }

            if (processSuccess && processedDataDict.Count > 0)
            {
                var data = new ProcessedTextureData(baseName, processedDataDict);
                return new FileProcessResult(ProcessStatus.Success, filename, Data: data);
            }
            else
            {
                 if (processedDataDict.Count > 0) foreach (var mat in processedDataDict.Values) mat.Dispose();
                 return new FileProcessResult(ProcessStatus.Failed, filename, Message: $"Processing logic failed or yielded no data");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error processing {Filename} in worker", filename);
            return new FileProcessResult(ProcessStatus.Failed, filename, Message: $"Processing error: {e.Message}");
        }
        finally
        {
            imgArray?.Dispose();
        }
    }

    public async Task<(int successful, int failed)> SaveProcessedDataAsync(
        List<FileProcessResult> results,
        IProgress<(int current, int total)>? progress = null)
    {
        _logger.LogInformation("Starting batch save...");
        var filesToSave = new ConcurrentBag<(Mat mat, string path, string originalFile)>();
        int totalFilesToAttemptSave = 0;

        foreach (var result in results)
        {
             if (result.Status == ProcessStatus.Success && result.Data != null)
            {
                foreach (var kvp in result.Data.Data)
                {
                    string suffixKey = kvp.Key;
                    Mat imgArray = kvp.Value;
                    string suffix = suffixKey switch {
                        "color" => "_color",
                        "alpha" => "_alpha",
                        "spec" => "_spec",
                        "roughness" => "_roughness",
                        "converted" => "_converted",
                        _ => ""
                    };

                    if (!string.IsNullOrEmpty(suffix))
                    {
                        string outFilename = Utils.InsertSuffix($"{result.Data.OriginalBaseName}.png", suffix);
                        string outPath = Path.Combine(OutputFolder, outFilename);
                        filesToSave.Add((imgArray, outPath, result.OriginalFileName));
                        totalFilesToAttemptSave++;
                    }
                    else
                    {
                        _logger.LogWarning("Unknown data key '{SuffixKey}' for file {OriginalFile}, cannot determine save suffix. Disposing.", suffixKey, result.OriginalFileName);
                        imgArray.Dispose();
                    }
                }
            }
            else if (result.Data?.Data != null)
            {
                 _logger.LogDebug("Disposing Mats from non-successful result for {OriginalFile} ({Status})", result.OriginalFileName, result.Status);
                 foreach(var mat in result.Data.Data.Values) mat.Dispose();
            }
        }


        if (!filesToSave.Any())
        {
            _logger.LogInformation("No successful results with data to save.");
            return (0, 0);
        }

        _logger.LogInformation("Submitting {Count} save tasks...", totalFilesToAttemptSave);

        int maxConcurrentSaves = MaxWorkers;
        using var saveSemaphore = new SemaphoreSlim(maxConcurrentSaves, maxConcurrentSaves);

        int successfulSaves = 0;
        int failedSaves = 0;
        int processedCount = 0;
        var saveTasks = new List<Task>();

        foreach (var item in filesToSave)
        {
            await saveSemaphore.WaitAsync(_cancellationToken);
             _cancellationToken.ThrowIfCancellationRequested();

            var task = Task.Run(async () =>
            {
                (Mat mat, string path, string originalFile) = item;
                try
                {
                    await SaveImageAsync(mat, path, _pngCompression, _logger, _cancellationToken);
                    Interlocked.Increment(ref successfulSaves);
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref failedSaves);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref failedSaves);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during save task for {Path}", path);
                    Interlocked.Increment(ref failedSaves);
                }
                finally
                {
                    mat.Dispose();
                    saveSemaphore.Release();
                    int currentProcessed = Interlocked.Increment(ref processedCount);
                    progress?.Report((currentProcessed, totalFilesToAttemptSave));
                }
            }, _cancellationToken);

            saveTasks.Add(task);
        }

        try
        {
            await Task.WhenAll(saveTasks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Save process was cancelled overall.");
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "One or more save tasks failed unexpectedly.");
        }


        _logger.LogInformation("Batch save complete. Saved: {SuccessfulSaves}, Failed: {FailedSaves}", successfulSaves, failedSaves);
        if (failedSaves > 0)
        {
            _logger.LogError("{FailedSaves} image(s) failed to save. Check logs above.", failedSaves);
        }

        return (successfulSaves, failedSaves);
    }

    public async Task<(int successful, int failed, int skipped)> ProcessAllAsync(IProgress<(int current, int total)>? progress = null)
    {
        List<string> inputFiles;
        try
        {
            inputFiles = Directory.EnumerateFiles(InputFolder)
                .Where(f => Constants.SupportedFormats.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith('.'))
                .ToList();
            _logger.LogInformation("Found {Count} supported image files.", inputFiles.Count);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Error scanning input folder {InputFolder}", InputFolder);
            return (0, 0, 0);
        }
        if (inputFiles.Count == 0)
        {
            _logger.LogWarning("No supported image files found in {InputFolder}", InputFolder);
            return (0, 0, 0);
        }
        _logger.LogInformation("Starting parallel processing with {MaxWorkers} workers...", MaxWorkers);

        var results = new ConcurrentBag<FileProcessResult>();
        int processedCount = 0;
        int totalFiles = inputFiles.Count;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxWorkers,
            CancellationToken = _cancellationToken
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Parallel.ForEachAsync(inputFiles, parallelOptions, async (filePath, token) =>
            {
                FileProcessResult result = await Task.Run(() => ProcessTextureReturnData(filePath), token);
                results.Add(result);
                int currentProcessed = Interlocked.Increment(ref processedCount);
                progress?.Report((currentProcessed, totalFiles));
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Processing was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled exception during parallel processing.");
        }

        stopwatch.Stop();
        _logger.LogInformation("Processing stage completed in {Elapsed}", Utils.FormatExecutionTime(stopwatch.Elapsed));

        int successfulCount = results.Count(r => r.Status == ProcessStatus.Success);
        int failedCount = results.Count(r => r.Status == ProcessStatus.Failed);
        int skippedCount = results.Count(r => r.Status == ProcessStatus.Skipped);

        _logger.LogInformation("Processing Summary - Success: {Success}, Failed: {Failed}, Skipped: {Skipped}", successfulCount, failedCount, skippedCount);

        foreach(var result in results.Where(r => r.Status != ProcessStatus.Success))
        {
            if (result.Status == ProcessStatus.Failed)
                _logger.LogError("Processing failed for {Filename}: {Message}", result.OriginalFileName, result.Message ?? "Unknown error");
            else if (result.Status == ProcessStatus.Skipped)
                 _logger.LogInformation("Skipped {Filename}: {Message}", result.OriginalFileName, result.Message ?? "No reason specified");
        }

        if (successfulCount > 0 && !_cancellationToken.IsCancellationRequested)
        {
            await SaveProcessedDataAsync(results.ToList(), progress);
        }
        else if (successfulCount > 0 && _cancellationToken.IsCancellationRequested)
        {
             _logger.LogWarning("Skipping save stage due to cancellation request.");
             foreach(var result in results)
             {
                  if (result.Data?.Data != null) foreach(var mat in result.Data.Data.Values) mat.Dispose();
             }
        }
        else
        {
            _logger.LogInformation("No textures processed successfully or cancellation requested, skipping save stage.");
            foreach(var result in results)
            {
                 if (result.Data?.Data != null) foreach(var mat in result.Data.Data.Values) mat.Dispose();
            }
        }

        return (successfulCount, failedCount, skippedCount);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}