# Tarkov Texture Converter

A C# utility for fast, parallel batch conversion of Unity/Tarkov texture maps (Normal, Diffuse, Gloss, SpecGlos). Includes automatic GLTF updating for Tarkin's Item Exporter workflow.

## Key Features

*   **Normal Maps:** Reorganizes channels (RGBA output).
*   **Diffuse Maps:** Splits into Color and optional Alpha maps.
*   **Gloss Maps:** Converts Gloss to Roughness maps (Standard mode only).
*   **SPECGLOS Mode (`--tarkin`):**
    *   Processes `_sg` textures (from Tarkin's Item Exporter) into Specular (`_spec.png`) and Roughness (`_roughness.png`) maps.
    *   Ignores standard Gloss maps (`_g`).
    *   Outputs only Color (`_color.png`) from Diffuse maps.
    *   Automatically updates texture paths in `.gltf` files in the input directory.
*   **Performance:** Uses multi-core processing. Optional higher PNG compression (`--optimize`).

## Prerequisites

*   [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
*   Windows (win-x64)

## Usage

1.  Build the `TarkovTextureConverter.Cli` project (or use the provided `.sln` in Visual Studio).
2.  Run from the command line within the `src\TarkovTextureConverter\TarkovTextureConverter.Cli` directory:

    ```sh
    dotnet run --project TarkovTextureConverter.Cli.csproj -- <input_folder> [options]
    ```

## CLI Options

*   `<input_folder>`: (Required) Path to the directory containing textures and (optionally) `.gltf` files.
*   `-t, --tarkin`: Enable SPECGLOS mode for Tarkin's Item Exporter workflow.
*   `-o, --optimize`: Enable higher (slower) PNG compression.
*   `-w, --workers <num>`: Set number of CPU worker threads (Default: system core count).

## Texture Processing & Output

Textures are identified by suffixes. Output filenames are based on the original name.

| Input Suffix                 | Standard Mode Output                     | SPECGLOS Mode (`--tarkin`) Output        |
| :--------------------------- | :--------------------------------------- | :--------------------------------------- |
| `_n`, `_normal`, `_nrm`      | `*_converted.png`                        | `*_converted.png`                        |
| `_d`, `_diff`, `_diffuse`, `_albedo` | `*_color.png` (+ `*_alpha.png` if needed) | `*_color.png`                            |
| `_g`, `_gloss`               | `*_roughness.png`                        | *Ignored*                                |
| `_sg`, `_specglos`           | *Ignored*                                | `*_spec.png` + `*_roughness.png`         |

## Supported Input Formats

PNG, JPG, JPEG, TIF, TIFF, BMP, TGA

## License

This project is licensed under the MIT license.