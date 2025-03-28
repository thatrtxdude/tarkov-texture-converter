using OpenCvSharp; // For Mat

namespace TarkovTextureConverter.Cli;

// Represents the result of processing a single texture file before saving
public record ProcessedTextureData(
    string OriginalBaseName,
    Dictionary<string, Mat> Data // Suffix key (e.g., "color", "alpha") to image data
);

// Represents the outcome of processing a single file
public record FileProcessResult(
    ProcessStatus Status,
    string OriginalFileName,
    ProcessedTextureData? Data = null, // Only non-null if Status is Success
    string? Message = null // Error or skip reason
);

public enum ProcessStatus
{
    Success,
    Failed,
    Skipped
}

// Arguments parsed from the command line
public record CliArguments(
    DirectoryInfo InputDirectory,
    bool TarkinMode,
    bool OptimizePng,
    int Workers
);