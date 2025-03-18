import os
import sys
import argparse
import logging
import signal
import time
from PIL import Image
import numpy as np
import tkinter as tk
from tkinter import filedialog, messagebox
from enum import Enum
from typing import Tuple, Optional, Set
from concurrent.futures import ProcessPoolExecutor, as_completed
from tqdm import tqdm

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
    SPECGLOS = "specglos"

class TextureProcessor:
    SUPPORTED_FORMATS: Set[str] = {'.png', '.jpg', '.jpeg', '.tif', '.tiff', '.bmp', '.tga'}
    DEFAULT_CHUNK_SIZE: int = 1024

    def __init__(self, input_folder: str, max_workers: Optional[int] = None, png_optimize: bool = False, tarkin_mode: bool = False):
        self.input_folder = input_folder
        self.output_folder = self._get_unique_output_folder()
        self.max_workers = max_workers if max_workers is not None else os.cpu_count() or 1
        self.png_optimize = png_optimize
        self.tarkin_mode = tarkin_mode
        os.makedirs(self.output_folder, exist_ok=True)
        logger.info(f"Output folder: {self.output_folder}")
        if self.tarkin_mode:
            logger.info("running with tarkin/specglos mode")

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
    def _get_texture_type(filename: str, tarkin_mode: bool) -> Optional[TextureType]:
        base_name = os.path.splitext(filename)[0].lower()
        # Diffuse textures are detected in both modes
        if any(base_name.endswith(suffix) for suffix in ["_diff", "_d", "_albedo"]):
            return TextureType.DIFFUSE
        if tarkin_mode:
            # In SPECGLOS mode, we ignore files ending with _gloss
            if base_name.endswith("_gloss"):
                return None  # Skip these files
            if base_name.endswith("_specglos"):
                return TextureType.SPECGLOS
            # Everything else is treated as a normal texture (for simplified normal map processing)
            return TextureType.NORMAL
        else:
            if base_name.endswith("_gloss"):
                return TextureType.GLOSS
            return TextureType.NORMAL

    @staticmethod
    def _process_normal_map_standard(img_array: np.ndarray) -> np.ndarray:
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, 0] = img_array[:, :, 3]  # A to R
        processed_array[:, :, 1] = img_array[:, :, 1]  # G remains
        processed_array[:, :, 2] = img_array[:, :, 0]  # R to B
        processed_array[:, :, 3] = 255  # Set Alpha to 255
        return processed_array

    @staticmethod
    def _process_normal_map_tarkin(img_array: np.ndarray) -> np.ndarray:
        if img_array.shape[2] == 4:
            return img_array.copy()
        else:
            h, w = img_array.shape[:2]
            new_array = np.zeros((h, w, 4), dtype=np.uint8)
            new_array[:, :, :3] = img_array
            new_array[:, :, 3] = 255
            return new_array

    @staticmethod
    def _process_diffuse_map_standard(img_array: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
        color_only = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        alpha_only = np.dstack((
            img_array[:, :, 3],
            img_array[:, :, 3],
            img_array[:, :, 3],
            np.full(img_array.shape[:2], 255, dtype=np.uint8)
        ))
        return color_only, alpha_only

    @staticmethod
    def _process_diffuse_map_tarkin(img_array: np.ndarray) -> np.ndarray:
        color_only = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        return color_only

    @staticmethod
    def _process_gloss_map(img_array: np.ndarray) -> np.ndarray:
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, :3] = 255 - img_array[:, :, :3]
        processed_array[:, :, 3] = img_array[:, :, 3]
        return processed_array

    @staticmethod
    def _process_specglos_map_split(img_array: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
        # Specular: use RGB channels and force full alpha
        specular = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        # Roughness: use inverted alpha channel as grayscale with full alpha
        inverted_alpha = 255 - img_array[:, :, 3]
        roughness = np.dstack((inverted_alpha, inverted_alpha, inverted_alpha, np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        return specular, roughness

    @staticmethod
    def _save_image(image_array: np.ndarray, output_path: str, png_optimize: bool):
        try:
            Image.fromarray(image_array, 'RGBA').save(output_path, 'PNG', optimize=png_optimize)
        except Exception as e:
            logger.error(f"Failed to save image to {output_path}: {e}")

    @staticmethod
    def process_texture(input_path: str, output_folder: str, png_optimize: bool, tarkin_mode: bool) -> Tuple[bool, Optional[str]]:
        try:
            with Image.open(input_path) as img:
                img = img.convert('RGBA') if img.mode != 'RGBA' else img
                img_array = np.asarray(img)
                filename = os.path.basename(input_path)
                texture_type = TextureProcessor._get_texture_type(filename, tarkin_mode)
                base_name = os.path.splitext(filename)[0]
                
                # Skip file if texture_type is None (e.g. _gloss textures in SPECGLOS mode)
                if texture_type is None:
                    logger.info(f"skipping file {filename} in SPECGLOS mode")
                    return (True, f"skipped {filename}")

                if texture_type == TextureType.DIFFUSE:
                    if tarkin_mode:
                        color_array = TextureProcessor._process_diffuse_map_tarkin(img_array)
                        color_path = os.path.join(output_folder, f"{base_name}_color.png")
                        TextureProcessor._save_image(color_array, color_path, png_optimize)
                        return (True, color_path)
                    else:
                        color_array, alpha_array = TextureProcessor._process_diffuse_map_standard(img_array)
                        color_path = os.path.join(output_folder, f"{base_name}_color.png")
                        alpha_path = os.path.join(output_folder, f"{base_name}_alpha.png")
                        TextureProcessor._save_image(color_array, color_path, png_optimize)
                        TextureProcessor._save_image(alpha_array, alpha_path, png_optimize)
                        return (True, color_path)
                elif texture_type == TextureType.SPECGLOS:
                    # Split the specular (RGB) and the inverted alpha (roughness)
                    specular, roughness = TextureProcessor._process_specglos_map_split(img_array)
                    specular_path = os.path.join(output_folder, f"{base_name}_spec.png")
                    roughness_path = os.path.join(output_folder, f"{base_name}_roughness.png")
                    TextureProcessor._save_image(specular, specular_path, png_optimize)
                    TextureProcessor._save_image(roughness, roughness_path, png_optimize)
                    return (True, specular_path)
                elif texture_type == TextureType.NORMAL:
                    if tarkin_mode:
                        processed_array = TextureProcessor._process_normal_map_tarkin(img_array)
                    else:
                        processed_array = TextureProcessor._process_normal_map_standard(img_array)
                    output_path = os.path.join(output_folder, f"{base_name}_converted.png")
                    TextureProcessor._save_image(processed_array, output_path, png_optimize)
                    return (True, output_path)
                elif texture_type == TextureType.GLOSS:
                    processed_array = TextureProcessor._process_gloss_map(img_array)
                    output_path = os.path.join(output_folder, f"{base_name}_roughness.png")
                    TextureProcessor._save_image(processed_array, output_path, png_optimize)
                    return (True, output_path)
                else:
                    return (False, "Unknown texture type")
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
                    self.png_optimize,
                    self.tarkin_mode
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
    hours, remainder = divmod(seconds, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{int(hours)}h {int(minutes)}m {seconds:.2f}s"

def main():
    parser = argparse.ArgumentParser(description="Tarkov Texture Converter")
    parser.add_argument("--tarkin", action="store_true", help="launch in SPECGLOS mode")
    args = parser.parse_args()

    root = tk.Tk()
    root.withdraw()
    folder_path = filedialog.askdirectory(title="Select Folder with Textures")
    if not folder_path:
        logger.info("No folder selected. Exiting.")
        return

    processor = TextureProcessor(
        folder_path,
        max_workers=os.cpu_count() or 1,
        png_optimize=False,
        tarkin_mode=args.tarkin
    )
    
    start_time = time.time()
    successful, failed = processor.process_all()
    end_time = time.time()
    
    elapsed_time = end_time - start_time
    formatted_time = format_execution_time(elapsed_time)

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
