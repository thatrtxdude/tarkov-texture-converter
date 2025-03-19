# Tarkov Texture Converter
A Python script for batch processing Unity/Tarkov texture maps (normal, diffuse, gloss, specglos) with parallel processing support.

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
### GUI Mode
1. Run the script without arguments:
```bash
python tarkov_texture_converter.py
```
2. Select input folder via GUI dialog
3. Processing happens automatically with progress visualization
4. Results appear in a uniquely named `converted_textures_X` subfolder

### CLI Mode
Process textures by directly specifying the input folder:
```bash
python tarkov_texture_converter.py /path/to/textures
```

### SPECGLOS Mode
IMPORTANT: This is supposed to be used with this: https://hub.sp-tarkov.com/files/file/2724-tarkin-item-exporter/#overview
```bash
python tarkov_texture_converter.py --tarkin
```
Or with a specific input folder:
```bash
python tarkov_texture_converter.py "/path/to/textures" --tarkin
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