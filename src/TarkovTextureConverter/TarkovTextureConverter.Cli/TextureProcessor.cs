using Microsoft.Extensions.Logging;
using OpenCvSharp; // OpenCV functions via Cv2 class
using System.Collections.Concurrent; // For ConcurrentBag
using System.Diagnostics; // For Stopwatch


namespace TarkovTextureConverter.Cli;

public class TextureProcessor : IDisposable
{
    private readonly ILogger<TextureProcessor> _logger;
    private readonly CliArguments _args;
    private readonly int _pngCompression;
    private readonly CancellationToken _cancellationToken;

    public string InputFolder { get; }
    public string OutputFolder { get; }
    public int MaxWorkers { get; } // Number of parallel processing tasks
    public bool TarkinMode => _args.TarkinMode;

    // Thread pool for saving images (I/O bound)
    // We don't explicitly create a separate pool like Python's ThreadPoolExecutor here,
    // C#'s Task infrastructure manages threads efficiently for I/O.
    // We control parallelism via ParallelOptions.

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
        while (Directory.Exists(outputFolder) || File.Exists(outputFolder)) // Check for file collision too
        {
            outputFolder = $"{baseOutput}_{counter}";
            counter++;
            if (counter > 1000) // Safety break
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

        // Iterate backwards through suffixes like _n, _d, _sg
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
                    return tarkinMode ? null : TextureType.Gloss; // Ignore gloss in Tarkin mode
                case "sg":
                case "specglos":
                    return tarkinMode ? TextureType.SpecGlos : null; // Only use specglos in Tarkin mode
            }
        }

        // Default behavior if no suffix found
        if (tarkinMode)
        {
            // In Tarkin mode, only process files with recognized suffixes (_d, _n, _sg)
            // _logger.LogDebug($"No relevant suffix for Tarkin mode in {filename}, skipping."); // Needs logger instance or static logger
            return null;
        }
        else
        {
            // In standard mode, assume unknown suffix means normal map
            // _logger.LogWarning($"No known suffix found for {filename}, assuming standard Normal Map."); // Needs logger instance or static logger
            return TextureType.Normal;
        }
    }

    private static Mat? LoadImage(string inputPath, ILogger logger)
    {
        try
        {
            // IMREAD_UNCHANGED attempts to load alpha channel if present
            Mat img = Cv2.ImRead(inputPath, ImreadModes.Unchanged);

            if (img == null || img.Empty())
            {
                logger.LogError("Failed to load image (OpenCV returned null/empty): {InputPath}", inputPath);
                // Python version had a fallback using imdecode, maybe add if needed:
                // try { byte[] buffer = File.ReadAllBytes(inputPath); img = Cv2.ImDecode(buffer, ImreadModes.Unchanged); } catch {}
                return null;
            }

            // Ensure RGBA format (BGRA in OpenCV terms)
            Mat rgbaImg;
            int channels = img.Channels();

            if (channels == 1) // Grayscale
            {
                rgbaImg = new Mat();
                Cv2.CvtColor(img, rgbaImg, ColorConversionCodes.GRAY2BGRA);
                img.Dispose(); // Dispose original
            }
            else if (channels == 3) // BGR
            {
                rgbaImg = new Mat();
                Cv2.CvtColor(img, rgbaImg, ColorConversionCodes.BGR2BGRA);
                img.Dispose(); // Dispose original
            }
            else if (channels == 4) // BGRA (already desired format)
            {
                 // Although it's BGRA, the processing logic expects RGBA channel order conceptually.
                 // Let's ensure consistency by converting BGRA -> RGBA if necessary later,
                 // OR adjust processing logic to work with BGRA order directly.
                 // For now, assume the processing functions handle the OpenCV BGRA order.
                 rgbaImg = img; // Keep original Mat
            }
            else
            {
                logger.LogError("Unsupported number of channels ({Channels}) in image: {InputPath}", channels, inputPath);
                img.Dispose();
                return null;
            }

            return rgbaImg; // Return the BGRA Mat
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading image {InputPath} with OpenCV", inputPath);
            return null;
        }
    }

    // --- Image Processing Methods (Static for potential reuse/testing) ---
    // Note: These methods now take and return Mat objects. Ensure disposal of intermediates.

    private static Mat ProcessNormalMapStandard(Mat imgArray, ILogger logger)
    {
        if (imgArray.Channels() != 4)
        {
            logger.LogWarning("Standard normal map processing expects 4 channels (BGRA) but got {Channels}. Attempting conversion.", imgArray.Channels());
            // Handle conversion if possible, otherwise return original or throw
            // This case should ideally be prevented by LoadImage ensuring 4 channels.
             return imgArray.Clone(); // Return a clone to avoid modifying input directly if needed elsewhere
        }

        // Input: BGRA (from OpenCV Load)
        // Tarkov Standard Normal (Unity DX Normal?): Usually stores Normal X in R, Normal Y in G.
        // Python code: processed[R] = source[A], processed[G] = source[G], processed[B]=255, processed[A]=255
        // This seems to be converting from a format where Normal X was in Alpha channel? (Unusual but possible)
        // Let's replicate the Python logic:
        // R <- A (alpha)
        // G <- G (green)
        // B <- 255 (white/blue)
        // A <- 255 (opaque)

        Mat processed = new Mat(imgArray.Rows, imgArray.Cols, MatType.CV_8UC4);
        Mat[] channels = Cv2.Split(imgArray); // B, G, R, A
        try
        {
             Mat[] outputChannels = new Mat[4];
             outputChannels[0] = channels[3];       // Output B gets Input A (index 3)
             outputChannels[1] = channels[1];       // Output G gets Input G (index 1)
             outputChannels[2] = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)); // Output R (index 2) is 255
             outputChannels[3] = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)); // Output A (index 3) is 255

             Cv2.Merge(outputChannels, processed);

             // Dispose temporary mats
             outputChannels[2].Dispose();
             outputChannels[3].Dispose();
        }
        finally
        {
            foreach(var ch in channels) ch.Dispose();
        }

        return processed; // BGRA format
    }

    private static Mat ProcessNormalMapTarkin(Mat imgArray, ILogger logger)
    {
         // Tarkin mode just seems to ensure alpha is 255.
         // Input is BGRA.
         Mat processed = imgArray.Clone(); // Work on a copy
         if (processed.Channels() == 4)
         {
             Mat[] channels = Cv2.Split(processed);
             try
             {
                channels[3].SetTo(Scalar.All(255)); // Set Alpha channel to 255
                Cv2.Merge(channels, processed);
             }
             finally
             {
                 foreach(var ch in channels) ch.Dispose();
             }
         }
         else if (processed.Channels() == 3) // Should not happen if LoadImage works, but handle defensively
         {
            logger.LogWarning("Tarkin normal map processing received 3 channels, converting to BGRA.");
            Mat tempRgba = new Mat();
            Cv2.CvtColor(processed, tempRgba, ColorConversionCodes.BGR2BGRA);
            processed.Dispose(); // Dispose the 3-channel original
            processed = tempRgba;
            // Alpha is implicitly 255 after conversion from BGR, but we can ensure
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
        // Input is BGRA
        if (imgArray.Channels() != 4)
        {
             // This indicates an issue upstream in LoadImage if it occurs.
             logger.LogError("Diffuse map standard processing expects 4 channels (BGRA), got {Channels}.", imgArray.Channels());
             // Return original color, no alpha separation possible/meaningful
             return (imgArray.Clone(), null);
        }

        Mat colorOnly = new Mat();
        Mat? alphaMap = null;
        Mat[] channels = Cv2.Split(imgArray); // B, G, R, A

        try
        {
            // Create color map (B, G, R, 255)
             using (Mat solidAlpha = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
             {
                 Mat[] colorChannels = { channels[0], channels[1], channels[2], solidAlpha };
                 Cv2.Merge(colorChannels, colorOnly);
             } // solidAlpha disposed here

             // Check if alpha channel has transparency
             // Using MinMaxLoc to find min value in alpha channel is efficient
             Cv2.MinMaxLoc(channels[3], out double minVal, out double maxVal);

             if (minVal < 255) // If any pixel is not fully opaque
             {
                 // Create alpha map (A, A, A, 255) - replicating Python logic
                 alphaMap = new Mat();
                 using (Mat solidAlpha = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
                 {
                     Mat[] alphaChannels = { channels[3], channels[3], channels[3], solidAlpha };
                     Cv2.Merge(alphaChannels, alphaMap);
                 } // solidAlpha disposed here
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
        // Tarkin Diffuse: Just BGR channels + Solid Alpha
        // Input is BGRA
        Mat colorOnly = new Mat();
         if (imgArray.Channels() == 4)
         {
             Mat[] channels = Cv2.Split(imgArray); // B, G, R, A
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
         else if (imgArray.Channels() == 3) // Should not happen
         {
            logger.LogWarning("Tarkin diffuse processing received 3 channels, converting to BGRA.");
            Cv2.CvtColor(imgArray, colorOnly, ColorConversionCodes.BGR2BGRA);
            // Alpha is implicitly 255 after conversion.
         }
         else
         {
            logger.LogError("Cannot process Tarkin diffuse map with {Channels} channels.", imgArray.Channels());
            return imgArray.Clone(); // Return copy of original on error
         }
        return colorOnly;
    }

    private static Mat ProcessGlossMap(Mat imgArray, ILogger logger)
    {
        // Standard Gloss to Roughness: Roughness = 1 - Gloss (often stored in R channel)
        // Python code: Inverts RGB, takes R channel of inverted, makes grayscale map (RRR, 255)
        // Input: BGRA (ensure 4 channels first)
        Mat sourceMat = imgArray;
        bool sourceNeedsDispose = false;

         if (imgArray.Channels() == 3) // Handle potential 3-channel input
         {
             logger.LogWarning("Gloss map processing received 3 channels, converting to BGRA.");
             sourceMat = new Mat();
             Cv2.CvtColor(imgArray, sourceMat, ColorConversionCodes.BGR2BGRA);
             sourceNeedsDispose = true;
             // Alpha is implicitly 255
         }
         else if (imgArray.Channels() != 4)
         {
             logger.LogError("Gloss map processing requires 3 or 4 channels, got {Channels}.", imgArray.Channels());
             return imgArray.Clone(); // Return copy on error
         }

        Mat roughnessMap = new Mat();
        try
        {
             // Invert BGR channels (leave Alpha alone)
             Mat invertedBgr = new Mat();
             Mat[] channels = Cv2.Split(sourceMat); // B G R A
             try
             {
                 using Mat bgr = new Mat(), inverted = new Mat();
                 Cv2.Merge(new[] { channels[0], channels[1], channels[2] }, bgr);
                 Cv2.BitwiseNot(bgr, inverted); // Invert BGR
                 Mat[] invertedChannels = Cv2.Split(inverted); // invB, invG, invR
                 try
                 {
                    // Python used inverted R (index 2) for roughness
                    Mat roughnessChannel = invertedChannels[2];
                    // Create Roughness map: (Roughness, Roughness, Roughness, 255)
                    // Use the inverted R channel as B, G, and R for the output.
                    using (Mat solidAlpha = new Mat(sourceMat.Size(), MatType.CV_8UC1, Scalar.All(255)))
                    {
                        Mat[] roughnessOutputChannels = { roughnessChannel, roughnessChannel, roughnessChannel, solidAlpha };
                        Cv2.Merge(roughnessOutputChannels, roughnessMap);
                    } // solidAlpha disposed
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

        return roughnessMap; // BGRA format
    }


    private static (Mat specular, Mat roughness) ProcessSpecGlosMapSplit(Mat imgArray, ILogger logger)
    {
        // SpecGlos: RGB = Specular, A = Gloss. Convert to PBR: Specular Map + Roughness Map
        // Output Specular: RGB, 255
        // Output Roughness: (1-Gloss), (1-Gloss), (1-Gloss), 255
        // Input: BGRA

        if (imgArray.Channels() != 4)
        {
            logger.LogError("SPECGLOS map processing requires 4 channels (BGRA), got {Channels}.", imgArray.Channels());
            // Return clones of original as fallback? Or throw? Let's return clones.
             return (imgArray.Clone(), imgArray.Clone());
        }

        Mat specularMap = new Mat();
        Mat roughnessMap = new Mat();
        Mat[] channels = Cv2.Split(imgArray); // B, G, R, A (A is Gloss)

        try
        {
            // Create Specular map (B, G, R, 255)
            using (Mat solidAlphaSpec = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
            {
                Mat[] specChannels = { channels[0], channels[1], channels[2], solidAlphaSpec };
                Cv2.Merge(specChannels, specularMap);
            } // solidAlphaSpec disposed

            // Create Roughness map (invA, invA, invA, 255) where invA = 255 - A
            using (Mat invertedAlpha = new Mat()) // invertedAlpha will hold 1-Gloss
            {
                Cv2.BitwiseNot(channels[3], invertedAlpha); // Invert Gloss channel (A) to get Roughness
                using (Mat solidAlphaRough = new Mat(imgArray.Size(), MatType.CV_8UC1, Scalar.All(255)))
                {
                     Mat[] roughChannels = { invertedAlpha, invertedAlpha, invertedAlpha, solidAlphaRough };
                     Cv2.Merge(roughChannels, roughnessMap);
                } // solidAlphaRough disposed
            } // invertedAlpha disposed
        }
        finally
        {
            foreach (var ch in channels) ch.Dispose();
        }

        return (specularMap, roughnessMap); // Both BGRA format
    }

    private static void SaveImage(Mat imageArray, string outputPath, int compression, ILogger logger)
    {
        string outputDir = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException("Invalid output path");
        string baseName = Path.GetFileNameWithoutExtension(outputPath);
        // Ensure .png extension
        string finalPath = Path.Combine(outputDir, baseName + ".png");

        try
        {
            Directory.CreateDirectory(outputDir); // Ensure directory exists

             // Input Mat is expected to be BGRA from processing steps.
             // Cv2.ImWrite expects BGR or BGRA for PNG.
             // If the input Mat isn't 3 or 4 channels, it's an error.
             if (imageArray.Channels() != 3 && imageArray.Channels() != 4)
             {
                 throw new ArgumentException($"Unsupported channel count for saving PNG: {imageArray.Channels()}");
             }

             // If saving grayscale was intended, it should be converted to BGR first.
             // Example: if (imageArray.Channels() == 1) Cv2.CvtColor(imageArray, imageToSave, ColorConversionCodes.GRAY2BGR);

            int[] compressionParams = { (int)ImwriteFlags.PngCompression, compression }; // Use ImwriteFlags enum
            bool success = Cv2.ImWrite(finalPath, imageArray, compressionParams);

            if (!success)
            {
                throw new IOException($"OpenCV ImWrite failed to save {finalPath}. Check permissions and disk space.");
            }
            // logger.LogDebug("Successfully saved image to {FinalPath}", finalPath); // Optional debug log
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save image to {FinalPath} using OpenCV", finalPath);
            // Re-throw or handle as needed, maybe return false? Let's re-throw for now.
            throw new IOException($"Failed to save image to {finalPath}: {ex.Message}", ex);
        }
    }


    /// <summary>
    /// Processes a single texture file: loads, determines type, processes, returns data.
    /// This is the unit of work for parallel execution.
    /// IMPORTANT: The returned Mats MUST be disposed by the caller (SaveProcessedData)
    /// </summary>
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

                    case TextureType.SpecGlos: // Only reachable if TarkinMode is true
                        var (spec, rough) = ProcessSpecGlosMapSplit(imgArray, _logger);
                        processedDataDict["spec"] = spec;
                        processedDataDict["roughness"] = rough;
                        break;

                    case TextureType.Normal:
                        processedDataDict["converted"] = TarkinMode
                            ? ProcessNormalMapTarkin(imgArray, _logger)
                            : ProcessNormalMapStandard(imgArray, _logger);
                        break;

                    case TextureType.Gloss: // Only reachable if TarkinMode is false
                        processedDataDict["roughness"] = ProcessGlossMap(imgArray, _logger);
                        break;

                    default:
                        // Should not happen
                        _logger.LogError("Internal logic error: Unknown texture type '{TextureType}' for file {Filename}", textureType.Value, filename);
                         processSuccess = false;
                        break;
                }
            }
            catch(Exception procEx)
            {
                 _logger.LogError(procEx, "Error during image processing logic for {Filename}", filename);
                 processSuccess = false;
                 // Clean up any partially processed Mats before returning failure
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
            // Dispose the original loaded image Mat
            imgArray?.Dispose();
        }
    }

     /// <summary>
    /// Saves the processed image data Mats collected from ProcessTextureReturnData.
    /// IMPORTANT: This method takes ownership and disposes the Mats in the results.
    /// </summary>
    /// <param name="results">List of results from processing.</param>
    /// <param name="progress">Optional progress reporting.</param>
    /// <returns>Tuple of (successful saves, failed saves)</returns>
    public async Task<(int successful, int failed)> SaveProcessedDataAsync(
        List<FileProcessResult> results,
        IProgress<(int current, int total)>? progress = null)
    {
        _logger.LogInformation("Starting batch save...");
        var saveTasks = new List<Func<Task>>();
        var filesToSave = new ConcurrentBag<(Mat mat, string path, string originalFile)>();
        int totalFilesToAttemptSave = 0;

        // Prepare save operations (extract Mat and determine output path)
        // We dispose the Mats *after* the save operation completes or fails.
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
                        _ => "" // Should not happen, maybe log warning
                    };

                    if (!string.IsNullOrEmpty(suffix))
                    {
                        string outFilename = Utils.InsertSuffix($"{result.Data.OriginalBaseName}.png", suffix); // Assuming PNG output
                        string outPath = Path.Combine(OutputFolder, outFilename);
                        filesToSave.Add((imgArray, outPath, result.OriginalFileName));
                        totalFilesToAttemptSave++;
                    }
                    else
                    {
                        _logger.LogWarning("Unknown data key '{SuffixKey}' for file {OriginalFile}, cannot determine save suffix. Disposing.", suffixKey, result.OriginalFileName);
                        imgArray.Dispose(); // Dispose if we don't know how to save it
                    }
                }
            }
            else if (result.Data?.Data != null) // Dispose mats from failed/skipped results if they exist (shouldn't normally)
            {
                 _logger.LogWarning("Disposing Mats from non-successful result for {OriginalFile} ({Status})", result.OriginalFileName, result.Status);
                 foreach(var mat in result.Data.Data.Values) mat.Dispose();
            }
        }

        if (!filesToSave.Any())
        {
            _logger.LogInformation("No successful results with data to save.");
            return (0, 0);
        }

        _logger.LogInformation("Submitting {Count} save tasks...", totalFilesToAttemptSave);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxWorkers, // Use same worker count for I/O for simplicity, could be tuned
            CancellationToken = _cancellationToken
        };

        int successfulSaves = 0;
        int failedSaves = 0;
        int processedCount = 0;

        try
        {
             await Parallel.ForEachAsync(filesToSave, parallelOptions, async (item, token) =>
             {
                 (Mat mat, string path, string originalFile) = item;
                 try
                 {
                     // Run synchronous OpenCV save on a thread pool thread
                     await Task.Run(() => SaveImage(mat, path, _pngCompression, _logger), token);
                     Interlocked.Increment(ref successfulSaves);
                     // logger.LogDebug("Saved {Path}", path);
                 }
                 catch (IOException ioEx)
                 {
                     // Logged within SaveImage
                     Interlocked.Increment(ref failedSaves);
                 }
                 catch (OperationCanceledException)
                 {
                     _logger.LogWarning("Save operation cancelled for {Path}", path);
                     Interlocked.Increment(ref failedSaves); // Count cancellation as failure for summary
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Unexpected error saving {Path}", path);
                     Interlocked.Increment(ref failedSaves);
                 }
                 finally
                 {
                     mat.Dispose(); // IMPORTANT: Dispose the Mat after saving (or attempting to save)
                     int currentProcessed = Interlocked.Increment(ref processedCount);
                     progress?.Report((currentProcessed, totalFilesToAttemptSave));
                 }
             });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Save process was cancelled.");
            // Count remaining items as failed? The loop stops, failedSaves will be partial.
            // Let's report what completed + failures so far.
        }


        _logger.LogInformation("Batch save complete. Saved: {SuccessfulSaves}, Failed: {FailedSaves}", successfulSaves, failedSaves);
        if (failedSaves > 0)
        {
            _logger.LogError("{FailedSaves} image(s) failed to save. Check logs above.", failedSaves);
        }

        return (successfulSaves, failedSaves);
    }


    /// <summary>
    /// Finds, processes, and saves all supported images in the input folder.
    /// </summary>
    /// <param name="progress">Callback for progress updates.</param>
    /// <returns>Tuple of (successful, failed, skipped) file counts.</returns>
    public async Task<(int successful, int failed, int skipped)> ProcessAllAsync(IProgress<(int current, int total)>? progress = null)
    {
        List<string> inputFiles;
        try
        {
            // Enumerate files matching supported extensions
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
                 // Run the synchronous processing method on a background thread
                 // This ensures CPU-bound OpenCV work doesn't block the main async flow for too long
                 // and allows Parallel.ForEachAsync to manage concurrency effectively.
                 FileProcessResult result = await Task.Run(() => ProcessTextureReturnData(filePath), token);
                 results.Add(result);

                 // Report progress after each file is processed
                 int currentProcessed = Interlocked.Increment(ref processedCount);
                 progress?.Report((currentProcessed, totalFiles));

                 // Log individual results immediately if needed (optional)
                 // switch(result.Status) { case ProcessStatus.Failed: _logger.LogError(...); break; ...}
            });
        }
        catch (OperationCanceledException)
        {
             _logger.LogWarning("Processing was cancelled.");
             // Results will be partial. Continue to save what was processed.
        }
        catch (Exception ex)
        {
             _logger.LogCritical(ex, "Unhandled exception during parallel processing.");
             // Potentially add results from the exception as failures?
             // For now, just log and proceed with collected results.
        }

        stopwatch.Stop();
        _logger.LogInformation("Processing stage completed in {Elapsed}", Utils.FormatExecutionTime(stopwatch.Elapsed));

        // Aggregate results
        int successfulCount = results.Count(r => r.Status == ProcessStatus.Success);
        int failedCount = results.Count(r => r.Status == ProcessStatus.Failed);
        int skippedCount = results.Count(r => r.Status == ProcessStatus.Skipped);

        _logger.LogInformation("Processing Summary - Success: {Success}, Failed: {Failed}, Skipped: {Skipped}", successfulCount, failedCount, skippedCount);

        // Log detailed failures/skips
        foreach(var result in results.Where(r => r.Status != ProcessStatus.Success))
        {
            if (result.Status == ProcessStatus.Failed)
                _logger.LogError("Processing failed for {Filename}: {Message}", result.OriginalFileName, result.Message ?? "Unknown error");
            else if (result.Status == ProcessStatus.Skipped)
                 _logger.LogInformation("Skipped {Filename}: {Message}", result.OriginalFileName, result.Message ?? "No reason specified");
        }

        if (successfulCount > 0)
        {
            // Pass results list - SaveProcessedDataAsync will handle disposal
            await SaveProcessedDataAsync(results.ToList(), progress); // Consider progress reporting for save phase too
        }
        else
        {
            _logger.LogInformation("No textures processed successfully, skipping save stage.");
            // Ensure disposal if save wasn't called
            foreach(var result in results)
            {
                 if (result.Data?.Data != null) foreach(var mat in result.Data.Data.Values) mat.Dispose();
            }
        }

        return (successfulCount, failedCount, skippedCount);
    }


    public void Dispose()
    {
        // Nothing explicit to dispose in this class itself currently,
        // but adhering to IDisposable is good practice if it held disposable resources directly.
        // Mats are handled within methods or passed out for the caller to dispose.
        GC.SuppressFinalize(this);
    }
}