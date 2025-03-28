import os
import sys
import logging
import time
import tkinter as tk
from tkinter import filedialog, messagebox, ttk, IntVar, BooleanVar, Text, Scrollbar
from typing import Optional
from threading import Thread
import subprocess

from .processor import TextureProcessor
from .gltf_utils import update_gltf_files
from .utils import format_execution_time
from .constants import RECOMMENDED_WORKERS

logger = logging.getLogger(__name__)

class TextHandler(logging.Handler):
    """A logging handler that directs records to a Tkinter Text widget."""
    def __init__(self, text_widget):
        logging.Handler.__init__(self)
        self.text_widget = text_widget
        self.widget_master = text_widget.master

    def emit(self, record):
        msg = self.format(record)
        def append_log():
            if self.widget_master and self.widget_master.winfo_exists() and self.text_widget.winfo_exists():
                current_state = self.text_widget.cget("state")
                self.text_widget.configure(state=tk.NORMAL)
                self.text_widget.insert(tk.END, msg + '\n')
                self.text_widget.see(tk.END)
                self.text_widget.configure(state=current_state)
        try:
             if self.widget_master and self.widget_master.winfo_exists():
                 self.widget_master.after(0, append_log)
        except tk.TclError: pass

class TextureProcessorApp(tk.Tk):
    """Main Tkinter application window for the Texture Converter."""
    def __init__(self):
        super().__init__()
        self.title("Tarkov Texture Converter")
        self.geometry("550x650")
        self.minsize(500, 500)
        self.configure(padx=15, pady=15)

        try:
            style = ttk.Style(self)
            available_themes = style.theme_names()
            desired_themes = ['clam', 'vista', 'xpnative', 'alt', 'default']
            for theme in desired_themes:
                 if theme in available_themes: style.theme_use(theme); break
        except Exception: pass

        self.folder_path = tk.StringVar()
        self.max_workers = IntVar(value=RECOMMENDED_WORKERS)
        self.png_optimize = BooleanVar(value=False)
        self.tarkin_mode = BooleanVar(value=False)
        self.is_processing = False
        self.processor_instance: Optional[TextureProcessor] = None

        self.create_widgets()
        self.setup_gui_logging()
        self.protocol("WM_DELETE_WINDOW", self.on_closing)

    def create_widgets(self):
        self.columnconfigure(0, weight=1)
        self.rowconfigure(3, weight=1)

        folder_frame = ttk.Frame(self)
        folder_frame.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        folder_frame.columnconfigure(1, weight=1)
        ttk.Label(folder_frame, text="Input Folder:").grid(row=0, column=0, padx=(0, 5), sticky="w")
        self.folder_entry = ttk.Entry(folder_frame, textvariable=self.folder_path, state="readonly")
        self.folder_entry.grid(row=0, column=1, padx=(0, 5), sticky="ew")
        self.browse_button = ttk.Button(folder_frame, text="Browse...", command=self.browse_folder)
        self.browse_button.grid(row=0, column=2, sticky="e")

        options_frame = ttk.LabelFrame(self, text="Processing Options")
        options_frame.grid(row=1, column=0, sticky="ew", pady=5)
        options_frame.columnconfigure(0, weight=1)
        worker_frame = ttk.Frame(options_frame)
        worker_frame.grid(row=0, column=0, sticky="ew", pady=5, padx=10)
        ttk.Label(worker_frame, text="CPU Workers:").pack(side=tk.LEFT)
        max_spinbox_workers = max(1, (os.cpu_count() or 1) * 4)
        self.worker_spinbox = ttk.Spinbox(worker_frame, from_=1, to=max_spinbox_workers, textvariable=self.max_workers, width=5, state="readonly")
        self.worker_spinbox.pack(side=tk.LEFT, padx=5)
        ttk.Label(worker_frame, text=f"(Cores: {os.cpu_count() or 'N/A'})").pack(side=tk.LEFT)
        self.optimize_check = ttk.Checkbutton(options_frame, text="Optimize PNG Output (Slower, Smaller Files)", variable=self.png_optimize)
        self.optimize_check.grid(row=1, column=0, sticky="w", pady=(0, 5), padx=10)
        self.tarkin_check = ttk.Checkbutton(options_frame, text="SPECGLOS Mode (Tarkin Exporter / GLTF Update)", variable=self.tarkin_mode)
        self.tarkin_check.grid(row=2, column=0, sticky="w", pady=(0, 5), padx=10)

        progress_frame = ttk.LabelFrame(self, text="Progress")
        progress_frame.grid(row=2, column=0, sticky="ew", pady=5)
        progress_frame.columnconfigure(0, weight=1)
        self.progress_bar = ttk.Progressbar(progress_frame, orient=tk.HORIZONTAL, length=100, mode='determinate')
        self.progress_bar.grid(row=0, column=0, sticky="ew", pady=5, padx=10)
        self.status_label = ttk.Label(progress_frame, text="Ready", anchor=tk.W)
        self.status_label.grid(row=1, column=0, sticky="ew", padx=10, pady=(0, 5))

        log_frame = ttk.LabelFrame(self, text="Process Log")
        log_frame.grid(row=3, column=0, sticky="nsew", pady=(10, 0))
        log_frame.rowconfigure(0, weight=1)
        log_frame.columnconfigure(0, weight=1)
        self.log_text = Text(log_frame, height=10, state=tk.DISABLED, wrap=tk.WORD, font=("Consolas", 9) or ("Courier New", 9))
        self.log_text.grid(row=0, column=0, sticky="nsew", padx=(5, 0), pady=5)
        scrollbar = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, command=self.log_text.yview)
        scrollbar.grid(row=0, column=1, sticky="ns", padx=(0, 5), pady=5)
        self.log_text.config(yscrollcommand=scrollbar.set)

        button_frame = ttk.Frame(self)
        button_frame.grid(row=4, column=0, sticky="e", pady=(10, 0))
        self.start_button = ttk.Button(button_frame, text="Start Processing", command=self.start_processing, state=tk.DISABLED)
        self.start_button.pack(side=tk.RIGHT)

    def setup_gui_logging(self):
        self.log_handler = TextHandler(self.log_text)
        log_format = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s', datefmt='%H:%M:%S')
        self.log_handler.setFormatter(log_format)
        logging.getLogger().addHandler(self.log_handler)
        self.log_handler.setLevel(logging.INFO)
        if logging.getLogger().getEffectiveLevel() > logging.INFO: logging.getLogger().setLevel(logging.INFO)

    def browse_folder(self):
        initial_dir = os.path.dirname(self.folder_path.get()) if self.folder_path.get() else None
        folder = filedialog.askdirectory(title="Select Folder Containing Textures", initialdir=initial_dir)
        if folder:
            self.folder_path.set(folder)
            self.start_button.config(state=tk.NORMAL)
            logger.info(f"Input folder selected: {folder}")

    def set_ui_state(self, processing: bool):
        self.is_processing = processing
        new_state = tk.DISABLED if processing else tk.NORMAL
        start_button_state = tk.NORMAL if self.folder_path.get() and not processing else tk.DISABLED
        spinbox_state = "readonly" if not processing else tk.DISABLED
        self.start_button.config(text="Processing..." if processing else "Start Processing", state=start_button_state)
        self.browse_button.config(state=new_state)
        self.worker_spinbox.config(state=spinbox_state)
        self.optimize_check.config(state=new_state)
        self.tarkin_check.config(state=new_state)

    def start_processing(self):
        if self.is_processing: messagebox.showwarning("Processing Active", "Processing already in progress.", parent=self); return
        folder_path = self.folder_path.get()
        if not folder_path or not os.path.isdir(folder_path): messagebox.showerror("Invalid Input", "Select valid input folder.", parent=self); self.start_button.config(state=tk.DISABLED); return

        try:
            self.log_text.configure(state=tk.NORMAL); self.log_text.delete(1.0, tk.END); self.log_text.configure(state=tk.DISABLED)
        except tk.TclError: logger.warning("Could not clear log widget.")

        logger.info("=" * 20 + " Starting New Processing Run " + "=" * 20)
        self.set_ui_state(True)
        self.progress_bar['value'] = 0
        self.status_label.config(text="Initializing...")
        self.update_idletasks()

        try:
            max_workers_val = self.max_workers.get()
            png_optimize_val = self.png_optimize.get()
            tarkin_mode_val = self.tarkin_mode.get()
            if max_workers_val < 1: max_workers_val = 1
            self.processor_instance = TextureProcessor(folder_path, max_workers=max_workers_val, png_optimize=png_optimize_val, tarkin_mode=tarkin_mode_val)
        except Exception as e:
            logger.error(f"Failed to initialize TextureProcessor: {e}", exc_info=True)
            messagebox.showerror("Initialization Error", f"Failed to set up processor:\n{e}", parent=self)
            self.set_ui_state(False); return

        processing_thread = Thread(target=self.run_processing_thread, args=(self.processor_instance,), daemon=True)
        processing_thread.start()

    def run_processing_thread(self, processor: TextureProcessor):
        start_time = time.time()
        successful, failed, skipped = 0, 0, 0

        def update_progress_gui(processed, total):
             if self.winfo_exists():
                 if total > 0:
                     progress = int((processed / total) * 100)
                     self.after(0, lambda: self.progress_bar.config(value=progress))
                     self.after(0, lambda: self.status_label.config(text=f"Processing: {processed}/{total} files"))
                 else: self.after(0, lambda: self.status_label.config(text="Processing: 0/0 files"))

        try:
            successful, failed, skipped = processor.process_all(progress_callback=update_progress_gui)
            end_time = time.time(); elapsed_time = end_time - start_time; formatted_time = format_execution_time(elapsed_time)
            result_summary = f"S:{successful} F:{failed} SK:{skipped}"
            log_message = f"Processing complete. Results - {result_summary}. Time: {formatted_time}"
            box_message = f"Processing complete.\n\nSuccessful: {successful}\nFailed: {failed}\nSkipped: {skipped}\n\nTime: {formatted_time}"

            if self.winfo_exists():
                self.after(0, lambda: self.status_label.config(text=f"Complete! {result_summary}"))
                self.after(0, lambda: logger.info(log_message))
                self.after(10, lambda: messagebox.showinfo("Processing Complete", box_message, parent=self))
                self.after(100, lambda: self.post_processing_tasks(processor, successful))

        except Exception as e:
            error_message = f"Unexpected error during processing: {e}"
            logger.error(error_message, exc_info=True)
            if self.winfo_exists():
                self.after(0, lambda: self.status_label.config(text="Error occurred! Check log."))
                self.after(10, lambda: messagebox.showerror("Runtime Error", error_message, parent=self))
        finally:
            if self.winfo_exists(): self.after(0, lambda: self.set_ui_state(False))
            self.processor_instance = None

    def post_processing_tasks(self, processor: TextureProcessor, successful_count: int):
        if not processor: logger.warning("Post-processing skipped: Processor invalid."); return
        try:
            if processor.tarkin_mode:
                logger.info("Tarkin mode: Running GLTF update check...")
                update_gltf_files(processor.input_folder, processor.output_folder)
                logger.info("GLTF update check finished.")
            output_folder = processor.output_folder
            if successful_count > 0 and os.path.exists(output_folder):
                logger.info(f"Attempting to open output folder: {output_folder}")
                try:
                    if sys.platform == 'win32': os.startfile(output_folder)
                    elif sys.platform == 'darwin': subprocess.run(['open', output_folder], check=True)
                    else: subprocess.run(['xdg-open', output_folder], check=True)
                except FileNotFoundError: logger.warning("Could not open folder: 'open' or 'xdg-open' not found.")
                except Exception as open_err:
                    logger.warning(f"Could not open folder '{output_folder}': {open_err}")
                    if self.winfo_exists(): self.after(10, lambda: messagebox.showwarning("Open Folder", f"Could not open output folder:\n{output_folder}\n\nError: {open_err}", parent=self))
        except Exception as post_err:
            logger.error(f"Error during post-processing: {post_err}", exc_info=True)
            if self.winfo_exists(): self.after(10, lambda: messagebox.showerror("Post-processing Error", f"Error after processing finished:\n{post_err}", parent=self))

    def on_closing(self):
        if self.is_processing:
            if messagebox.askokcancel("Quit", "Processing is active. Quit anyway?", parent=self):
                logger.warning("Forcing quit during active processing.")
                self.destroy(); sys.exit(1)
        else: logger.info("Exiting application."); self.destroy(); sys.exit(0)