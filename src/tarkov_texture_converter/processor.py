import os
import logging
import cv2
import numpy as np
from typing import Tuple, Optional, Dict, List
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor, as_completed
from tqdm import tqdm

from .constants import (
    TextureType,
    SUPPORTED_FORMATS,
    DEFAULT_OUTPUT_SUBFOLDER,
    PNG_COMPRESSION_DEFAULT,
    PNG_COMPRESSION_OPTIMIZED,
    RECOMMENDED_WORKERS
)
from .utils import insert_suffix # Import needed for save_processed_data

logger = logging.getLogger(__name__)

class TextureProcessor:
    """
    Handles loading, processing, and saving of texture images based on detected types.
    """

    def __init__(self, input_folder: str, max_workers: Optional[int] = None, png_optimize: bool = False, tarkin_mode: bool = False):
        if not os.path.isdir(input_folder):
            raise ValueError(f"Input folder does not exist or is not a directory: {input_folder}")

        self.input_folder: str = input_folder
        self.max_workers: int = max_workers if max_workers is not None and max_workers > 0 else RECOMMENDED_WORKERS
        self.png_compression: int = PNG_COMPRESSION_OPTIMIZED if png_optimize else PNG_COMPRESSION_DEFAULT
        self.tarkin_mode: bool = tarkin_mode
        self.output_folder: str = self._get_unique_output_folder()
        self.io_workers: int = self.max_workers

        logger.info(f"Initialized TextureProcessor:")
        logger.info(f"  Input Folder: {self.input_folder}")
        logger.info(f"  Output Folder: {self.output_folder}")
        logger.info(f"  CPU Workers: {self.max_workers}")
        logger.info(f"  PNG Optimization: {png_optimize} (Compression Level: {self.png_compression})")
        logger.info(f"  SPECGLOS (Tarkin) Mode: {self.tarkin_mode}")

    def _get_unique_output_folder(self) -> str:
        """Creates a unique output folder path within the input directory."""
        base_output = os.path.join(self.input_folder, DEFAULT_OUTPUT_SUBFOLDER)
        counter = 1
        output_folder = base_output
        while os.path.exists(output_folder):
            output_folder = f"{base_output}_{counter}"
            counter += 1
        try:
            os.makedirs(output_folder, exist_ok=True)
            return output_folder
        except OSError as e:
            logger.error(f"Failed to create output directory {output_folder}: {e}")
            raise

    @staticmethod
    def _get_texture_type(filename: str, tarkin_mode: bool) -> Optional[TextureType]:
        """Determines the texture type based on filename suffix."""
        base_name = os.path.splitext(filename)[0].lower()
        parts = base_name.split('_')
        for part in reversed(parts):
            if part in ["n", "normal", "nrm"]: return TextureType.NORMAL
            if part in ["d", "diff", "diffuse", "albedo"]: return TextureType.DIFFUSE
            if part in ["g", "gloss", "gls"]: return None if tarkin_mode else TextureType.GLOSS
            if part in ["sg", "specglos"]: return TextureType.SPECGLOS if tarkin_mode else None
        if tarkin_mode:
            logger.debug(f"No relevant suffix for Tarkin mode in {filename}, skipping.")
            return None
        else:
            logger.warning(f"No known suffix found for {filename}, assuming standard Normal Map.")
            return TextureType.NORMAL

    @staticmethod
    def _load_image(input_path: str) -> Optional[np.ndarray]:
        """Loads an image using OpenCV, ensuring RGBA format."""
        try:
            img = cv2.imread(input_path, cv2.IMREAD_UNCHANGED)
            if img is None:
                try:
                     with open(input_path, "rb") as f: chunk = f.read()
                     img = cv2.imdecode(np.frombuffer(chunk, dtype=np.uint8), cv2.IMREAD_UNCHANGED)
                     if img is None:
                          logger.error(f"Failed to load image (OpenCV None even with imdecode): {input_path}")
                          return None
                except Exception as decode_err:
                     logger.error(f"Failed to load image (OpenCV None) and imdecode failed: {decode_err} | Path: {input_path}")
                     return None

            if img.ndim == 2: img = cv2.cvtColor(img, cv2.COLOR_GRAY2RGBA)
            elif img.shape[2] == 3: img = cv2.cvtColor(img, cv2.COLOR_BGR2RGBA)
            elif img.shape[2] == 4: img = cv2.cvtColor(img, cv2.COLOR_BGRA2RGBA)
            else:
                logger.error(f"Unsupported number of channels ({img.shape[2]}) in image: {input_path}")
                return None
            return img
        except Exception as e:
            logger.error(f"Error loading image {input_path} with OpenCV: {e}", exc_info=False)
            return None

    @staticmethod
    def _process_normal_map_standard(img_array: np.ndarray) -> np.ndarray:
        if img_array.shape[2] != 4:
            logger.warning(f"Standard normal map processing expects 4 channels (RGBA) but got {img_array.shape}.")
            if img_array.shape[2] == 3:
                img_array = cv2.cvtColor(img_array, cv2.COLOR_RGB2RGBA)
                img_array[:, :, 3] = 255
            else: return img_array
        processed_array = np.empty_like(img_array, dtype=np.uint8)
        processed_array[:, :, 0] = img_array[:, :, 3]
        processed_array[:, :, 1] = img_array[:, :, 1]
        processed_array[:, :, 2] = 255
        processed_array[:, :, 3] = 255
        return processed_array

    @staticmethod
    def _process_normal_map_tarkin(img_array: np.ndarray) -> np.ndarray:
        processed_array = img_array.copy()
        if processed_array.shape[2] == 4: processed_array[:, :, 3] = 255
        elif processed_array.shape[2] == 3:
             processed_array = cv2.cvtColor(processed_array, cv2.COLOR_RGB2RGBA)
             processed_array[:, :, 3] = 255
        return processed_array

    @staticmethod
    def _process_diffuse_map_standard(img_array: np.ndarray) -> Tuple[np.ndarray, Optional[np.ndarray]]:
        alpha_map = None
        if img_array.shape[2] == 4:
            color_only = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
            alpha_channel = img_array[:, :, 3]
            if np.any(alpha_channel < 255):
                alpha_map = np.dstack((alpha_channel, alpha_channel, alpha_channel, np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        elif img_array.shape[2] == 3:
             color_only = cv2.cvtColor(img_array, cv2.COLOR_RGB2RGBA)
             color_only[:, :, 3] = 255
        else: raise ValueError(f"Cannot process diffuse map with {img_array.shape[2]} channels.")
        return color_only, alpha_map

    @staticmethod
    def _process_diffuse_map_tarkin(img_array: np.ndarray) -> np.ndarray:
        if img_array.shape[2] == 4:
            color_only = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        elif img_array.shape[2] == 3:
             color_only = cv2.cvtColor(img_array, cv2.COLOR_RGB2RGBA)
             color_only[:, :, 3] = 255
        else: raise ValueError(f"Cannot process Tarkin diffuse map with {img_array.shape[2]} channels.")
        return color_only

    @staticmethod
    def _process_gloss_map(img_array: np.ndarray) -> np.ndarray:
        if img_array.shape[2] < 3: raise ValueError(f"Gloss map processing expects at least 3 channels, got {img_array.shape}.")
        if img_array.shape[2] == 3:
             img_array = cv2.cvtColor(img_array, cv2.COLOR_RGB2RGBA)
             img_array[:, :, 3] = 255
        inverted_rgb = 255 - img_array[:, :, :3]
        roughness_channel = inverted_rgb[:, :, 0]
        roughness_map = np.dstack((roughness_channel, roughness_channel, roughness_channel, np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        return roughness_map

    @staticmethod
    def _process_specglos_map_split(img_array: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
        if img_array.shape[2] != 4: raise ValueError(f"SPECGLOS map processing requires 4 channels, got {img_array.shape}.")
        specular = np.dstack((img_array[:, :, :3], np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        inverted_alpha = 255 - img_array[:, :, 3]
        roughness = np.dstack((inverted_alpha, inverted_alpha, inverted_alpha, np.full(img_array.shape[:2], 255, dtype=np.uint8)))
        return specular, roughness

    @staticmethod
    def _save_image(image_array: np.ndarray, output_path: str, compression: int):
        """Saves a NumPy array as a PNG image using OpenCV."""
        output_dir = os.path.dirname(output_path)
        base_name = os.path.basename(output_path)
        name, _ = os.path.splitext(base_name)
        output_path = os.path.join(output_dir, name + ".png")
        try:
            os.makedirs(output_dir, exist_ok=True)
            img_to_save = None
            if image_array.ndim == 3:
                if image_array.shape[2] == 4: img_to_save = cv2.cvtColor(image_array, cv2.COLOR_RGBA2BGRA)
                elif image_array.shape[2] == 3: img_to_save = cv2.cvtColor(image_array, cv2.COLOR_RGB2BGR)
                else: raise ValueError(f"Unsupported channel count for saving: {image_array.shape[2]}")
            elif image_array.ndim == 2: img_to_save = cv2.cvtColor(image_array, cv2.COLOR_GRAY2BGR)
            else: raise ValueError(f"Unsupported image dimensions for saving: {image_array.ndim}")

            params = [cv2.IMWRITE_PNG_COMPRESSION, compression]
            success = cv2.imwrite(output_path, img_to_save, params)
            if not success: raise RuntimeError(f"OpenCV imwrite failed to save {output_path}")
        except Exception as e:
            logger.error(f"Failed to save image to {output_path} using OpenCV: {e}", exc_info=False)
            raise RuntimeError(f"Failed to save image to {output_path}: {e}") from e

    @staticmethod
    def process_texture_return_data(input_path: str, tarkin_mode: bool) -> Tuple[str, str, Optional[Dict[str, np.ndarray]], Optional[str]]:
        """Loads, processes a single texture, and returns the processed data."""
        filename = os.path.basename(input_path)
        base_name = os.path.splitext(filename)[0]
        try:
            img_array = TextureProcessor._load_image(input_path)
            if img_array is None: return ("failed", base_name, None, f"Failed to load image {filename}")

            texture_type = TextureProcessor._get_texture_type(filename, tarkin_mode)
            if texture_type is None: return ("skipped", base_name, None, f"Skipped {filename} (suffix/mode)")

            processed_data: Dict[str, np.ndarray] = {}
            if texture_type == TextureType.DIFFUSE:
                if tarkin_mode: processed_data["color"] = TextureProcessor._process_diffuse_map_tarkin(img_array)
                else:
                    color_array, alpha_array = TextureProcessor._process_diffuse_map_standard(img_array)
                    processed_data["color"] = color_array
                    if alpha_array is not None: processed_data["alpha"] = alpha_array
            elif texture_type == TextureType.SPECGLOS:
                specular, roughness = TextureProcessor._process_specglos_map_split(img_array)
                processed_data["spec"] = specular
                processed_data["roughness"] = roughness
            elif texture_type == TextureType.NORMAL:
                if tarkin_mode: processed_data["converted"] = TextureProcessor._process_normal_map_tarkin(img_array)
                else: processed_data["converted"] = TextureProcessor._process_normal_map_standard(img_array)
            elif texture_type == TextureType.GLOSS:
                processed_data["roughness"] = TextureProcessor._process_gloss_map(img_array)
            else: return ("failed", base_name, None, f"Internal logic error: Unknown texture type '{texture_type}'")

            return ("success", base_name, processed_data, None)
        except Exception as e:
            logger.error(f"Error processing {filename} in worker: {e}", exc_info=False)
            return ("failed", base_name, None, f"Processing error: {e}")

    def save_processed_data(self, results: List[Tuple[str, str, Optional[Dict[str, np.ndarray]], Optional[str]]], progress_callback=None):
        """Saves the processed image data using a ThreadPoolExecutor."""
        logger.info(f"Starting batch save...")
        save_tasks = []
        files_to_save_count = 0
        successful_saves = 0
        failed_saves = 0

        for status, base_name, data_dict, error_msg in results:
            if status == "success" and data_dict:
                for suffix_key, img_array in data_dict.items():
                    if suffix_key == "color": suffix = "_color"
                    elif suffix_key == "alpha": suffix = "_alpha"
                    elif suffix_key == "spec": suffix = "_spec"
                    elif suffix_key == "roughness": suffix = "_roughness"
                    elif suffix_key == "converted": suffix = "_converted"
                    else: continue
                    out_filename = insert_suffix(f"{base_name}.png", suffix)
                    out_path = os.path.join(self.output_folder, out_filename)
                    save_tasks.append((img_array, out_path, self.png_compression))
                    files_to_save_count += 1

        if not save_tasks:
            logger.info("No successful results with data to save.")
            return 0, 0

        logger.info(f"Submitting {files_to_save_count} save tasks to {self.io_workers} I/O threads.")
        pbar_save = None
        if progress_callback is None: pbar_save = tqdm(total=files_to_save_count, desc="Saving Images ", unit="file", leave=False)

        with ThreadPoolExecutor(max_workers=self.io_workers, thread_name_prefix='SaveWorker') as executor:
            future_to_path = {executor.submit(self._save_image, *task): task[1] for task in save_tasks}
            for future in as_completed(future_to_path):
                output_path = future_to_path[future]
                try:
                    future.result()
                    successful_saves += 1
                except Exception as e:
                    failed_saves += 1
                    logger.error(f"Save task failed for an image. Path: {output_path} | See specific error above.")
                finally:
                    num_completed = successful_saves + failed_saves
                    if progress_callback: progress_callback(num_completed, files_to_save_count)
                    elif pbar_save: pbar_save.update(1)

        if pbar_save: pbar_save.close()
        logger.info(f"Batch save complete. Saved: {successful_saves}, Failed: {failed_saves}")
        if failed_saves > 0: logger.error(f"{failed_saves} image(s) failed to save. Check logs.")
        return successful_saves, failed_saves

    def process_all(self, progress_callback=None) -> Tuple[int, int, int]:
        """Finds, processes, and saves all supported images."""
        input_files = []
        try:
            with os.scandir(self.input_folder) as entries:
                for entry in entries:
                    if entry.is_file() and not entry.name.startswith('.'):
                        ext = os.path.splitext(entry.name.lower())[1]
                        if ext in SUPPORTED_FORMATS: input_files.append(entry.path)
            logger.info(f"Found {len(input_files)} supported image files.")
        except FileNotFoundError: logger.error(f"Input folder not found: {self.input_folder}"); return (0, 0, 0)
        except OSError as e: logger.error(f"Error scanning input folder {self.input_folder}: {e}"); return (0, 0, 0)
        if not input_files: logger.warning("No supported image files found."); return (0, 0, 0)

        logger.info(f"Starting parallel processing with {self.max_workers} workers...")
        processed_results = []
        successful_count, failed_count, skipped_count = 0, 0, 0
        total_files = len(input_files)
        processed_files_count = 0
        pbar_process = None
        if progress_callback is None: pbar_process = tqdm(total=total_files, desc="Processing Textures", unit="file", leave=True)

        with ProcessPoolExecutor(max_workers=self.max_workers) as executor:
            future_to_path = {executor.submit(self.process_texture_return_data, fp, self.tarkin_mode): fp for fp in input_files}
            for future in as_completed(future_to_path):
                input_path = future_to_path[future]
                filename = os.path.basename(input_path)
                try:
                    status, base_name, data_dict, error_msg = future.result()
                    processed_results.append((status, base_name, data_dict, error_msg))
                    if status == "success": successful_count += 1
                    elif status == "failed": failed_count += 1; logger.error(f"Processing failed for {filename}: {error_msg}")
                    elif status == "skipped": skipped_count += 1; logger.info(f"Skipped {filename}: {error_msg}")
                except Exception as e:
                    failed_count += 1
                    logger.critical(f"Critical error retrieving result for {filename}: {e}", exc_info=True)
                    processed_results.append(("failed", os.path.splitext(filename)[0], None, f"Critical error: {e}"))
                finally:
                    processed_files_count += 1
                    if progress_callback: progress_callback(processed_files_count, total_files)
                    elif pbar_process: pbar_process.update(1)

        if pbar_process: pbar_process.close()
        logger.info(f"Processing stage complete. Results - Success: {successful_count}, Failed: {failed_count}, Skipped: {skipped_count}")

        if successful_count > 0: self.save_processed_data(processed_results, progress_callback=None)
        else: logger.info("No textures processed successfully, skipping save stage.")
        return successful_count, failed_count, skipped_count