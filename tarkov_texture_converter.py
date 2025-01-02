import os
import logging
import signal
from PIL import Image
import numpy as np
import tkinter as tk
from tkinter import filedialog, messagebox
from enum import Enum
from typing import Tuple, Optional
from concurrent.futures import ThreadPoolExecutor, as_completed
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
    SUPPORTED_FORMATS: frozenset[str] = frozenset({'.png', '.jpg', '.jpeg', '.tif', '.tiff', '.bmp'})
    DEFAULT_CHUNK_SIZE: int = 1024

    def __init__(self, input_folder: str, max_workers: Optional[int] = None, png_optimize: bool = True):
        self.input_folder = input_folder
        self.output_folder = self._get_unique_output_folder()
        self.max_workers = max_workers if max_workers is not None else max(1, (os.cpu_count() or 1) // 2)
        self.png_optimize = png_optimize
        os.makedirs(self.output_folder, exist_ok=True)
        logger.info(f"Output folder: {self.output_folder}")

    def _get_unique_output_folder(self) -> str:
        base_output = os.path.join(self.input_folder, "converted_textures")
        if not os.path.exists(base_output):
            return base_output
        counter = 1
        while os.path.exists(f"{base_output}_{counter}"):
            counter += 1
        return f"{base_output}_{counter}"

    @lru_cache(maxsize=None)
    def _get_texture_type(self, filename: str) -> Optional[TextureType]:
        base_name = os.path.splitext(filename)[0].lower()
        if base_name.endswith("_diff"):
            return TextureType.DIFFUSE
        elif base_name.endswith("_gloss"):
            return TextureType.GLOSS
        else:
            return TextureType.NORMAL

    def _process_normal_map(self, img_array: np.ndarray) -> np.ndarray:
        r, g, b, a = img_array[:, :, 0], img_array[:, :, 1], img_array[:, :, 2], img_array[:, :, 3]
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, 0] = a
        processed_array[:, :, 1] = b
        processed_array[:, :, 2] = r
        processed_array[:, :, 3] = 255
        return processed_array

    def _process_diffuse_map(self, img_array: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
        rgb = img_array[:, :, :3]
        alpha = img_array[:, :, 3]
        color_only = np.empty((img_array.shape[0], img_array.shape[1], 4), dtype=np.uint8)
        color_only[:, :, :3] = rgb
        color_only[:, :, 3] = 255
        alpha_only = np.empty_like(color_only, dtype=np.uint8)
        alpha_only[:, :, 0:3] = alpha[:, :, None]
        alpha_only[:, :, 3] = 255
        return color_only, alpha_only

    def _process_gloss_map(self, img_array: np.ndarray) -> np.ndarray:
        inverted_rgb = 255 - img_array[:, :, :3]
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, :3] = inverted_rgb
        processed_array[:, :, 3] = img_array[:, :, 3]
        return processed_array

    def _save_image(self, image_array: np.ndarray, output_path: str):
        try:
            image = Image.fromarray(image_array, 'RGBA')
            image.save(output_path, 'PNG', optimize=self.png_optimize)
        except Exception as e:
            logger.error(f"Failed to save image to {output_path}: {e}")

    def process_texture(self, input_path: str) -> Tuple[bool, Optional[str]]:
        try:
            img = Image.open(input_path)
            if img.mode != 'RGBA':
                img = img.convert('RGBA')

            img_array = np.asarray(img)
            texture_type = self._get_texture_type(os.path.basename(input_path))
            base_name = os.path.splitext(os.path.basename(input_path))[0]

            if texture_type == TextureType.DIFFUSE:
                color_array, alpha_array = self._process_diffuse_map(img_array)
                color_path = os.path.join(self.output_folder, f"{base_name}_color.png")
                alpha_path = os.path.join(self.output_folder, f"{base_name}_alpha.png")
                self._save_image(color_array, color_path)
                self._save_image(alpha_array, alpha_path)
                return True, color_path

            elif texture_type == TextureType.GLOSS:
                processed_array = self._process_gloss_map(img_array)
                output_path = os.path.join(self.output_folder, f"{base_name}_roughness.png")
                self._save_image(processed_array, output_path)

            elif texture_type == TextureType.NORMAL:
                processed_array = self._process_normal_map(img_array)
                output_path = os.path.join(self.output_folder, f"{base_name}_converted.png")
                self._save_image(processed_array, output_path)

            else:
                return False, "Unknown texture type"

            return True, output_path

        except Exception as e:
            logger.error(f"Error processing {os.path.basename(input_path)}: {e}")
            return False, f"Error processing {os.path.basename(input_path)}: {e}"

    def process_all(self) -> Tuple[int, int]:
        filenames = {
            f for f in os.listdir(self.input_folder)
            if os.path.splitext(f.lower())[1] in self.SUPPORTED_FORMATS
        }

        successful_count, failed_count = 0, 0

        if not filenames:
            logger.info("No supported texture files found in the input folder.")
            return 0, 0

        logger.info(f"Found {len(filenames)} textures to process using {self.max_workers} threads.")

        with ThreadPoolExecutor(max_workers=self.max_workers) as executor:
            future_to_filename = {
                executor.submit(self.process_texture, os.path.join(self.input_folder, filename)): filename
                for filename in filenames
            }

            with tqdm(total=len(filenames), desc="Processing Textures") as pbar:
                for future in as_completed(future_to_filename):
                    filename = future_to_filename[future]
                    try:
                        success, result = future.result()
                        if success:
                            successful_count += 1
                        else:
                            failed_count += 1
                            logger.error(f"Error processing {filename}: {result}")
                    except Exception as e:
                        failed_count += 1
                        logger.error(f"Unexpected error processing {filename}: {e}")
                    pbar.update(1)

        return successful_count, failed_count

def main():
    root = tk.Tk()
    root.withdraw()

    logger.info("Please select the folder containing the textures to process.")
    folder_path = filedialog.askdirectory(title="Select Folder with Textures")

    if not folder_path:
        logger.info("No folder selected. Exiting.")
        return

    num_cores = os.cpu_count() or 1
    half_cores = max(1, num_cores // 2)
    processor = TextureProcessor(folder_path, max_workers=half_cores)

    logger.info(f"Using {processor.max_workers} threads for processing.")
    successful, failed = processor.process_all()

    message = f"Processing complete!\nSuccessful: {successful}\nFailed: {failed}"
    logger.info(message)
    root.destroy()  # Ensure the Tkinter root window is destroyed before exit
    logger.info("Application will now close.")
    exit(0)

if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        error_msg = f"An unexpected error occurred: {str(e)}"
        logger.error(error_msg)
        messagebox.showerror("Error", error_msg)
        exit(1)
