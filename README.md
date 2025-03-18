# Tarkov Texture Converter

A Python script for batch processing Unity/Tarkov texture maps (normal, diffuse, gloss) with parallel processing support.

## Features
- Converts normal maps to RGBA format with channel reorganization
- Splits diffuse maps into color+alpha textures
- Converts gloss maps to roughness maps
- Preserves alpha channels
- Multi-core processing
- PNG optimization option

## Prerequisites
- Python 3.7+
- `Pillow`, `numpy`, `tqdm`

Install dependencies:
```bash
pip install pillow numpy tqdm
```

## Usage
1. Run the script:
```bash
python tarkov_texture_converter.py
```
2. Select input folder via GUI dialog
3. Processing happens automatically
4. Results appear in `converted_textures` subfolder

Output structure:
- Normal maps: `*_converted.png`
- Diffuse maps: `*_color.png` + `*_alpha.png`
- Roughness maps: `*_roughness.png`

Add `png_optimize=True` to `TextureProcessor` initialization for smaller PNG files (slower processing).

## Usage - SPECGLOS Mode
IMPORTANT: This is supposed to be used with this: https://hub.sp-tarkov.com/files/file/2724-tarkin-item-exporter/#overview
Run the script with following argument appended at the end:
```bash
python tarkov_texture_converter.py --tarkin
```


## Notes
- Supported formats: PNG, JPG, JPEG, TIF, TIFF, BMP, TGA
- Files must follow naming convention: `*_diff`, `*_gloss` for special processing
- Normal maps without suffixes get channel reorganization (A→R, G→G, R→B)

## License
This project is released under the MIT License.
