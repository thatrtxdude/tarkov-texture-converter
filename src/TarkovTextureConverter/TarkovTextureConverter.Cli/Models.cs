using OpenCvSharp; 

namespace TarkovTextureConverter.Cli;

public record ProcessedTextureData(
    string OriginalBaseName,
    Dictionary<string, Mat> Data 
);

public record FileProcessResult(
    ProcessStatus Status,
    string OriginalFileName,
    ProcessedTextureData? Data = null, 
    string? Message = null 
);

public enum ProcessStatus
{
    Success,
    Failed,
    Skipped
}

public record CliArguments(
    DirectoryInfo InputDirectory,
    bool TarkinMode,
    bool OptimizePng,
    int Workers
);