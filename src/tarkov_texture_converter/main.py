import os
import sys
import argparse
import logging
import tkinter as tk
from tkinter import messagebox
from multiprocessing import freeze_support
import time

try:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    package_root = os.path.dirname(script_dir)
    project_root = os.path.dirname(package_root)
except NameError: pass

try:
    from tarkov_texture_converter.utils import setup_logging, setup_signal_handlers, format_execution_time
    from tarkov_texture_converter.processor import TextureProcessor
    from tarkov_texture_converter.gltf_utils import update_gltf_files
    from tarkov_texture_converter.gui import TextureProcessorApp
    from tarkov_texture_converter.constants import RECOMMENDED_WORKERS
except ImportError as e:
     try:
          from .utils import setup_logging, setup_signal_handlers, format_execution_time
          from .processor import TextureProcessor
          from .gltf_utils import update_gltf_files
          from .gui import TextureProcessorApp
          from .constants import RECOMMENDED_WORKERS
     except ImportError as inner_e:
          print(f"Fatal Error: Could not import modules. Errors: {e} | {inner_e}")
          print("Try running from project root: `python -m src.tarkov_texture_converter.main`")
          sys.exit(1)

setup_logging()
setup_signal_handlers()
logger = logging.getLogger(__name__)

def run_cli(args):
    """Runs the texture conversion process in command-line mode."""
    folder_path = args.input_folder
    if not folder_path: logger.error("Input folder path required for CLI."); sys.exit(1)
    if not os.path.isdir(folder_path): logger.error(f"Input folder not found: '{folder_path}'"); sys.exit(1)
    folder_path = os.path.abspath(folder_path)

    logger.info("="*20 + " Running in Command Line Mode " + "="*20)
    logger.info(f"Input Folder: {folder_path}")
    logger.info(f"SPECGLOS (Tarkin) Mode: {'Enabled' if args.tarkin else 'Disabled'}")
    logger.info(f"PNG Optimization: {'Enabled' if args.optimize else 'Disabled'}")
    num_workers = args.workers if args.workers is not None and args.workers > 0 else RECOMMENDED_WORKERS
    logger.info(f"Using {num_workers} CPU workers.")

    try:
        processor = TextureProcessor(folder_path, max_workers=num_workers, png_optimize=args.optimize, tarkin_mode=args.tarkin)
        start_time = time.time()
        successful, failed, skipped = processor.process_all()
        end_time = time.time(); elapsed_time = end_time - start_time; formatted_time = format_execution_time(elapsed_time)

        logger.info("-" * 60)
        logger.info(f"Processing Summary:")
        logger.info(f"  Input Folder: {processor.input_folder}")
        logger.info(f"  Output Folder: {processor.output_folder}")
        logger.info(f"  Successful: {successful}")
        logger.info(f"  Failed:     {failed}")
        logger.info(f"  Skipped:    {skipped}")
        logger.info(f"  Total Time: {formatted_time}")
        logger.info("-" * 60)

        if processor.tarkin_mode:
             logger.info("Tarkin mode enabled, running GLTF update check...")
             update_gltf_files(processor.input_folder, processor.output_folder)
             logger.info("GLTF update check finished.")

    except ValueError as ve: logger.error(f"Initialization Error: {ve}"); sys.exit(1)
    except Exception as e: logger.critical(f"Critical CLI error: {e}", exc_info=True); sys.exit(1)

def run_gui(args):
    """Launches the Tkinter GUI application."""
    logger.info("No input folder provided, launching GUI...")
    try:
        app = TextureProcessorApp()
        if args.tarkin: app.tarkin_mode.set(True)
        if args.optimize: app.png_optimize.set(True)
        if args.workers is not None and args.workers > 0:
             max_gui_workers = (os.cpu_count() or 1) * 4
             cli_workers = min(args.workers, max_gui_workers)
             if cli_workers != args.workers: logger.warning(f"Workers ({args.workers}) > GUI limit ({max_gui_workers}), using {cli_workers}.")
             app.max_workers.set(cli_workers)
        app.mainloop()
    except Exception as e:
        logger.critical(f"Failed to launch/run GUI: {e}", exc_info=True)
        try: # Fallback error message
            root = tk.Tk(); root.withdraw(); messagebox.showerror("Fatal GUI Error", f"Could not start:\n{e}\n\nCheck logs."); root.destroy()
        except Exception: print(f"CRITICAL: GUI failed & fallback message failed: {e}", file=sys.stderr)
        sys.exit(1)

def main():
    """Parses arguments and decides mode."""
    parser = argparse.ArgumentParser(
        description="Tarkov Texture Converter: Processes textures, optionally updates GLTF.",
        formatter_class=argparse.RawTextHelpFormatter
    )
    parser.add_argument("input_folder", nargs='?', help="Input folder path (GUI if omitted).")
    parser.add_argument("--tarkin", action="store_true", help="Enable SPECGLOS mode & GLTF update.")
    parser.add_argument("--optimize", action="store_true", help="Enable higher PNG compression (slower).")
    parser.add_argument("--workers", type=int, default=None, metavar="N", help=f"Num worker processes (Default: {RECOMMENDED_WORKERS})")
    args = parser.parse_args()
    if args.input_folder: run_cli(args)
    else: run_gui(args)

if __name__ == "__main__":
    freeze_support() # Needed for PyInstaller/cx_Freeze bundling
    try: main()
    except Exception as e:
        logger.critical(f"Unhandled top-level exception: {e}", exc_info=True)
        try: # Final fallback message
             root = tk.Tk(); root.withdraw(); messagebox.showerror("Fatal Error", f"Critical error:\n{e}\n\nCheck logs."); root.destroy()
        except Exception: print(f"CRITICAL: Unhandled exception & fallback message failed: {e}", file=sys.stderr)
        sys.exit(1)
    finally: logging.shutdown()