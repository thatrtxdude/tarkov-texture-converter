# Tarkov Texture Converter

A Python utility for batch processing Unity/Tarkov texture maps with modern parallel processing and improved GLTF updating support.

## Overview

This tool processes texture maps—normal, diffuse, gloss, and specglos—by reorganizing channels, splitting diffuse maps into separate color and alpha images, and converting gloss maps to roughness maps. It also supports specialized SPECGLOS mode, which updates GLTF files to work with exports from Tarkin's Item Exporter.

## Features

- **Normal Map Conversion:** Reorganizes normal map channels and enforces RGBA output.
- **Diffuse Map Processing:** Splits diffuse maps to output separate color and, if needed, alpha maps.
- **Gloss Map Conversion:** Converts gloss maps to roughness maps.
- **Parallel Processing:** Utilizes multi-core processing for faster conversions.
- **PNG Optimization:** Optionally applies higher PNG compression (at the cost of processing speed).

## Prerequisites

- Python 3.7+
- Dependencies: `opencv-python`, `numpy`, `tqdm`

Install dependencies with:

```bash
pip install opencv-python numpy tqdm
```

## Usage
### GUI Mode
1. Run the script without arguments:
```bash
cd src
python -m tarkov_texture_converter.main
```
2. Select input folder via GUI dialog
3. Processing happens automatically with progress visualization
4. Results appear in a uniquely named `converted_textures_X` subfolder

### CLI Mode
Process textures by directly specifying the input folder:
```bash
cd src
python -m tarkov_texture_converter.main /path/to/textures
```

### SPECGLOS Mode
IMPORTANT: This is supposed to be used with this: https://hub.sp-tarkov.com/files/file/2724-tarkin-item-exporter/#overview
```bash
cd src
python -m tarkov_texture_converter.main --tarkin
```
Or with a specific input folder:
```bash
cd src
python -m tarkov_texture_converter.main "/path/to/textures" --tarkin
```

## Output Format
- Normal maps: `*_converted.png`
- Diffuse maps: `*_color.png` + `*_alpha.png` (standard mode) or just `*_color.png` (SPECGLOS mode)
- Gloss maps: `*_roughness.png` (not processed in SPECGLOS mode)
- SPECGLOS maps: `*_spec.png` + `*_roughness.png` (SPECGLOS mode only)

## Texture Type Detection
Files are automatically processed based on their suffix:
- Normal maps: `*_n`, `*_normal`, `*_nrm`
- Diffuse maps: `*_d`, `*_diff`, `*_diffuse`, `*_albedo`
- Gloss maps: `*_g`, `*_gloss` (ignored in SPECGLOS mode)
- SpecGlos maps: `*_sg`, `*_specglos` (SPECGLOS mode only)

## Supported Formats
PNG, JPG, JPEG, TIF, TIFF, BMP, TGA

## License
This project is released under the MIT License.