# Tarkov Texture Converter

A C# utility for batch processing Unity/Tarkov texture maps with modern parallel processing and built-in GLTF updating support.

## Overview

This tool processes texture maps—normal, diffuse, gloss, and specglos—by reorganizing channels, splitting diffuse maps into separate color and, if necessary, alpha images, and converting gloss maps to roughness maps. It also has support for Tarkin's Item Exporter, it automatically converts the SPECGLOS format that gets exported and updates the .gltf files to use the converted textures.

## Features

- **Normal Map Conversion:** Reorganizes normal map channels and ensures RGBA output.
- **Diffuse Map Processing:** Splits diffuse maps to produce separate color (and optional alpha) images.
- **Gloss Map Conversion:** Converts gloss maps to roughness maps.
- **Parallel Processing:** Leverages multi-core processing for faster conversions.
- **PNG Optimization:** Optionally applies higher PNG compression (at the cost of processing speed).
- **GLTF Updating:** Automatically revises GLTF files to update texture URIs after conversion (in SPECGLOS mode).

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Supported platforms: Windows (win-x64)

## Build & Run

### Using Visual Studio

1. Open the solution file ([TarkovTextureConverter.sln](e:\tarkov-texture-converter\src\TarkovTextureConverter\TarkovTextureConverter.sln)).
2. Build the solution.
3. Run the `TarkovTextureConverter.Cli` project.

### Using Command Line

Open a terminal in the `src\TarkovTextureConverter\TarkovTextureConverter.Cli` folder and run:

```sh
dotnet run --project TarkovTextureConverter.Cli.csproj -- /path/to/textures
```

## CLI Options

- **Input Folder:**  
  Specify the directory containing texture images.  
  Example: `/path/to/textures`

- **SPECGLOS Mode:**  
  Enable SPECGLOS mode (for use with Tarkin's Item Exporter) using the `--tarkin` (or `-t`) option.  
  Example:
  ```sh
  dotnet run --project TarkovTextureConverter.Cli.csproj -- /path/to/textures --tarkin
  ```

- **PNG Optimization:**  
  Enable higher PNG compression with the `--optimize` (or `-o`) flag.

- **Workers:**  
  Set the number of CPU worker threads with the `--workers` (or `-w`) option.  
  Default is determined by the number of processor cores.

## Output Format

- **Normal Maps:** Output as `*_converted.png`
- **Diffuse Maps:**  
  - Standard mode: `*_color.png` + `*_alpha.png`
  - SPECGLOS mode: Only `*_color.png`
- **Gloss Maps:** Output as `*_roughness.png` (not processed in SPECGLOS mode)
- **SPECGLOS Maps:** Output as `*_spec.png` + `*_roughness.png` (when using SPECGLOS mode)

## Texture Type Detection

Texture maps are processed automatically based on file suffixes:
- **Normal Maps:** `*_n`, `*_normal`, `*_nrm`
- **Diffuse Maps:** `*_d`, `*_diff`, `*_diffuse`, `*_albedo`
- **Gloss Maps:** `*_g`, `*_gloss` (ignored in SPECGLOS mode)
- **SpecGlos Maps:** `*_sg`, `*_specglos` (processed only in SPECGLOS mode)

## Supported Formats

PNG, JPG, JPEG, TIF, TIFF, BMP, TGA

## License

This project is released under the MIT License.