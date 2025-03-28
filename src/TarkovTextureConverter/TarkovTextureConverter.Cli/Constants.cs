using System.Collections.Immutable; // For ImmutableHashSet

namespace TarkovTextureConverter.Cli;

public static class Constants
{
    // Use ImmutableHashSet for thread-safe, read-only set
    public static readonly ImmutableHashSet<string> SupportedFormats =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, // Case-insensitive comparison
            ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".tga");

    public const string DefaultOutputSubfolder = "converted_textures";

    // OpenCV PNG Compression Flags (0=Default/Fastest, 9=Best/Slowest)
    public const int PngCompressionDefault = 1; // OpenCV default is actually 3, 1 seems closer to python's 0 intention speed-wise? Test this. Python 0 is no compression. Let's use 1 as a balanced default.
    public const int PngCompressionOptimized = 9; // Max compression

    // Recommend Environment.ProcessorCount workers
    public static int RecommendedWorkers => Environment.ProcessorCount > 0 ? Environment.ProcessorCount : 1;
}

public enum TextureType
{
    Normal,
    Diffuse,
    Gloss,     // Only used when TarkinMode is false
    SpecGlos   // Only used when TarkinMode is true
}