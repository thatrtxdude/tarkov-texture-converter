import os
from enum import Enum
from typing import Set

SUPPORTED_FORMATS: Set[str] = {'.png', '.jpg', '.jpeg', '.tif', '.tiff', '.bmp', '.tga'}

class TextureType(Enum):
    NORMAL = "normal"
    DIFFUSE = "diff"
    GLOSS = "gloss"
    SPECGLOS = "specglos"

DEFAULT_OUTPUT_SUBFOLDER = "converted_textures"

PNG_COMPRESSION_DEFAULT = 0
PNG_COMPRESSION_OPTIMIZED = 9

RECOMMENDED_WORKERS = os.cpu_count() or 1