using System.Collections.Immutable; 

namespace TarkovTextureConverter.Cli;

public static class Constants
{

    public static readonly ImmutableHashSet<string> SupportedFormats =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, 
            ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".tga");

    public const string DefaultOutputSubfolder = "converted_textures";

    public const int PngCompressionDefault = 1; 
    public const int PngCompressionOptimized = 9; 

    public static int RecommendedWorkers => Environment.ProcessorCount > 0 ? Environment.ProcessorCount : 1;
}

public enum TextureType
{
    Normal,
    Diffuse,
    Gloss,     
    SpecGlos   
}