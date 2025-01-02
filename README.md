# Texture Processor for Tarkov Assets

## Overview
This tool processes texture files used in Tarkov assets, converting them into standardized formats for easier handling and editing. It supports normal maps, diffuse maps, and gloss maps, applying specific transformations for each type.

## Features
- Automatic identification of texture types based on filenames.
- Efficient multi-threaded processing using `ThreadPoolExecutor`.
- Support for common texture formats: `.png`, `.jpg`, `.jpeg`, `.tif`, `.tiff`, and `.bmp`.
- Outputs organized into a separate folder with unique naming to avoid overwrites.
- Specifically optimized for handling Tarkov assets.

## Requirements
- Python 3.8+
- Required libraries:
  - `Pillow`
  - `numpy`
  - `tqdm`

Install dependencies using pip:
```bash
pip install pillow numpy tqdm
```

## How to Use
1. Run the script using Python:
   ```bash
   python texture_processor.py
   ```

2. When prompted, select the folder containing the texture files you want to process.

3. The tool will:
   - Identify and process supported textures.
   - Save the processed textures in a new folder within the selected directory.

4. Once processing is complete, the application will close automatically.

### Notes
- Make sure your texture files are named according to their type (e.g., `_diff`, `_gloss`) for proper detection.
- The tool utilizes half of your available CPU cores by default for optimal performance.

## Output
- Normal maps are converted to a standard format.
- Diffuse maps are split into color and alpha components.
- Gloss maps are inverted.
- Processed textures are saved as `.png` files.

## License
This project is released under the MIT License.


