import os
import logging
import signal
import time  # Added time module
from PIL import Image
import numpy as np
import tkinter as tk
from tkinter import filedialog, messagebox
from enum import Enum
from typing import Tuple, Optional, Set
from concurrent.futures import ProcessPoolExecutor, as_completed
from tqdm import tqdm
from functools import lru_cache

# Setup logging
logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)

def handle_exit(signum, frame):
    logger.info("Gracefully shutting down...")
    exit(0)

signal.signal(signal.SIGINT, handle_exit)
signal.signal(signal.SIGTERM, handle_exit)

class TextureType(Enum):
    NORMAL = "normal"
    DIFFUSE = "diff"
    GLOSS = "gloss"

class TextureProcessor:
    SUPPORTED_FORMATS: Set[str] = {'.png', '.jpg', '.jpeg', '.tif', '.tiff', '.bmp', '.tga'}
    DEFAULT_CHUNK_SIZE: int = 1024

    def __init__(self, input_folder: str, max_workers: Optional[int] = None, png_optimize: bool = False):
        self.input_folder = input_folder
        self.output_folder = self._get_unique_output_folder()
        self.max_workers = max_workers if max_workers is not None else os.cpu_count() or 1
        self.png_optimize = png_optimize
        os.makedirs(self.output_folder, exist_ok=True)
        logger.info(f"Output folder: {self.output_folder}")

    def _get_unique_output_folder(self) -> str:
        base_output = os.path.join(self.input_folder, "converted_textures")
        counter = 1
        while True:
            output_folder = f"{base_output}_{counter}" if counter > 1 else base_output
            if not os.path.exists(output_folder):
                os.makedirs(output_folder)
                return output_folder
            counter += 1

    @staticmethod
    @lru_cache(maxsize=None)
    def _get_texture_type(filename: str) -> Optional[TextureType]:
        base_name = os.path.splitext(filename)[0].lower()
        if base_name.endswith("_diff"):
            return TextureType.DIFFUSE
        elif base_name.endswith("_gloss"):
            return TextureType.GLOSS
        else:
            return TextureType.NORMAL

    @staticmethod
    def _process_normal_map(img_array: np.ndarray) -> np.ndarray:
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, 0] = img_array[:, :, 3]  # A to R
        processed_array[:, :, 1] = img_array[:, :, 1]  # G remains
        processed_array[:, :, 2] = img_array[:, :, 0]  # R to B
        processed_array[:, :, 3] = 255  # Alpha to 255
        return processed_array

    @staticmethod
    def _process_diffuse_map(img_array: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
        color_only = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        alpha_only = np.dstack((img_array[:, :, 3], img_array[:, :, 3], img_array[:, :, 3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        return color_only, alpha_only

    @staticmethod
    def _process_gloss_map(img_array: np.ndarray) -> np.ndarray:
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, :3] = 255 - img_array[:, :, :3]
        processed_array[:, :, 3] = img_array[:, :, 3]
        return processed_array

    @staticmethod
    def _save_image(image_array: np.ndarray, output_path: str, png_optimize: bool):
        try:
            Image.fromarray(image_array, 'RGBA').save(output_path, 'PNG', optimize=png_optimize)
        except Exception as e:
            logger.error(f"Failed to save image to {output_path}: {e}")

    @staticmethod
    def process_texture(input_path: str, output_folder: str, png_optimize: bool) -> Tuple[bool, Optional[str]]:
        try:
            with Image.open(input_path) as img:
                img = img.convert('RGBA') if img.mode != 'RGBA' else img
                img_array = np.asarray(img)
                filename = os.path.basename(input_path)
                texture_type = TextureProcessor._get_texture_type(filename)
                base_name = os.path.splitext(filename)[0]

                if texture_type == TextureType.DIFFUSE:
                    color_array, alpha_array = TextureProcessor._process_diffuse_map(img_array)
                    color_path = os.path.join(output_folder, f"{base_name}_color.png")
                    alpha_path = os.path.join(output_folder, f"{base_name}_alpha.png")
                    TextureProcessor._save_image(color_array, color_path, png_optimize)
                    TextureProcessor._save_image(alpha_array, alpha_path, png_optimize)
                    return (True, color_path)
                elif texture_type == TextureType.GLOSS:
                    processed_array = TextureProcessor._process_gloss_map(img_array)
                    output_path = os.path.join(output_folder, f"{base_name}_roughness.png")
                    TextureProcessor._save_image(processed_array, output_path, png_optimize)
                elif texture_type == TextureType.NORMAL:
                    processed_array = TextureProcessor._process_normal_map(img_array)
                    output_path = os.path.join(output_folder, f"{base_name}_converted.png")
                    TextureProcessor._save_image(processed_array, output_path, png_optimize)
                else:
                    return (False, "Unknown texture type")
                return (True, output_path)
        except Exception as e:
            logger.error(f"Error processing {os.path.basename(input_path)}: {e}")
            return (False, str(e))

    def process_all(self) -> Tuple[int, int]:
        filenames = []
        with os.scandir(self.input_folder) as entries:
            for entry in entries:
                if entry.is_file():
                    ext = os.path.splitext(entry.name.lower())[1]
                    if ext in self.SUPPORTED_FORMATS:
                        filenames.append(entry.name)
        if not filenames:
            logger.info("No supported files found.")
            return (0, 0)

        logger.info(f"Processing {len(filenames)} files with {self.max_workers} workers.")
        successful_count = 0
        failed_count = 0

        with ProcessPoolExecutor(max_workers=self.max_workers) as executor:
            futures = {
                executor.submit(
                    TextureProcessor.process_texture,
                    os.path.join(self.input_folder, filename),
                    self.output_folder,
                    self.png_optimize
                ): filename for filename in filenames
            }

            with tqdm(total=len(filenames), desc="Processing Textures") as pbar:
                for future in as_completed(futures):
                    filename = futures[future]
                    try:
                        success, result = future.result()
                        if success:
                            successful_count += 1
                        else:
                            failed_count += 1
                            logger.error(f"Failed {filename}: {result}")
                    except Exception as e:
                        failed_count += 1
                        logger.error(f"Unexpected error in {filename}: {e}")
                    pbar.update(1)

        return (successful_count, failed_count)

def format_execution_time(seconds: float) -> str:
    """Convert seconds to human-readable time string (hours, minutes, seconds)."""
    hours, remainder = divmod(seconds, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{int(hours)}h {int(minutes)}m {seconds:.2f}s"

def main():
    root = tk.Tk()
    root.withdraw()
    folder_path = filedialog.askdirectory(title="Select Folder with Textures")
    if not folder_path:
        logger.info("No folder selected. Exiting.")
        return

    processor = TextureProcessor(folder_path, max_workers=os.cpu_count() or 1)
    
    # Time measurement
    start_time = time.time()
    successful, failed = processor.process_all()
    end_time = time.time()
    
    elapsed_time = end_time - start_time
    formatted_time = format_execution_time(elapsed_time)

    # Update messages
    result_message = f"Successful: {successful}\nFailed: {failed}\nExecution Time: {formatted_time}"
    logger.info(f"Processing complete. {result_message.replace(chr(10), ', ')}")
    messagebox.showinfo("Complete", result_message)
    
    root.destroy()
    exit(0)

if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        messagebox.showerror("Error", str(e))
        exit(1)