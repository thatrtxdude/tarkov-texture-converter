import os
import json
import logging
from typing import Dict

from .constants import TextureType
from .processor import TextureProcessor
from .utils import insert_suffix

logger = logging.getLogger(__name__)

def update_gltf_files(input_folder: str, output_folder_abs_path: str):
    """Scans and updates GLTF files for Tarkin mode."""
    if not os.path.isdir(input_folder):
        logger.error(f"GLTF Update: Input folder '{input_folder}' not found.")
        return

    output_folder_basename = os.path.basename(output_folder_abs_path)
    logger.info(f"Scanning for GLTF files in '{input_folder}' relative to '{output_folder_basename}'...")
    gltf_found_count, updated_gltf_count = 0, 0

    try:
        for entry in os.scandir(input_folder):
            if entry.is_file() and entry.name.lower().endswith(".gltf"):
                 gltf_found_count += 1
                 gltf_path = entry.path
                 logger.info(f"Processing GLTF file: {gltf_path}")
                 try:
                     with open(gltf_path, "r", encoding='utf-8') as f: gltf_data = json.load(f)
                 except Exception as e:
                     logger.error(f"  Error loading/decoding {gltf_path}: {e}")
                     continue

                 materials = gltf_data.get("materials", [])
                 images = gltf_data.get("images", [])
                 textures = gltf_data.get("textures", [])
                 file_updated = False
                 image_uri_map: Dict[int, str] = {}

                 for img_index, img in enumerate(images):
                     old_uri = img.get("uri", "")
                     if not old_uri or old_uri.startswith("data:"): continue
                     old_uri_norm = old_uri.replace("\\", "/")
                     old_filename = os.path.basename(old_uri_norm)
                     texture_type = TextureProcessor._get_texture_type(old_filename, tarkin_mode=True)
                     new_uri_base = ""
                     if texture_type == TextureType.DIFFUSE: new_uri_base = insert_suffix(old_filename, "_color") + ".png"
                     elif texture_type == TextureType.SPECGLOS: new_uri_base = insert_suffix(old_filename, "_roughness") + ".png"
                     elif texture_type == TextureType.NORMAL: new_uri_base = insert_suffix(old_filename, "_converted") + ".png"

                     if new_uri_base:
                         new_uri = os.path.join(output_folder_basename, new_uri_base).replace("\\", "/")
                         if img.get("uri") != new_uri:
                             logger.info(f"    Image [{img_index}]: Updating URI '{img.get('uri')}' -> '{new_uri}'")
                             img["uri"] = new_uri
                             image_uri_map[img_index] = new_uri
                             file_updated = True

                 used_extensions = set(gltf_data.get("extensionsUsed", []))
                 required_extensions = set(gltf_data.get("extensionsRequired", []))
                 removed_specgloss_extension = False

                 for mat_index, mat in enumerate(materials):
                     mat_name = mat.get("name", f"Material_{mat_index}")
                     pbr_metallic_roughness = mat.get("pbrMetallicRoughness", {})
                     extensions = mat.get("extensions", {})
                     spec_gloss = extensions.get("KHR_materials_pbrSpecularGlossiness")
                     mat_updated_internally = False

                     if spec_gloss:
                         # Base Color
                         tex_info = spec_gloss.get("diffuseTexture")
                         tex_index = tex_info.get("index") if tex_info else None
                         if tex_index is not None and 0 <= tex_index < len(textures):
                             source_img_index = textures[tex_index].get("source")
                             if source_img_index in image_uri_map:
                                 pbr_metallic_roughness["baseColorTexture"] = tex_info
                                 mat_updated_internally = True
                         if "diffuseFactor" in spec_gloss:
                            pbr_metallic_roughness["baseColorFactor"] = spec_gloss["diffuseFactor"]
                            mat_updated_internally = True
                         # Metallic/Roughness
                         tex_info = spec_gloss.get("specularGlossinessTexture")
                         tex_index = tex_info.get("index") if tex_info else None
                         if tex_index is not None and 0 <= tex_index < len(textures):
                             source_img_index = textures[tex_index].get("source")
                             if source_img_index in image_uri_map:
                                 pbr_metallic_roughness["metallicRoughnessTexture"] = tex_info
                                 pbr_metallic_roughness.setdefault("metallicFactor", 0.0)
                                 pbr_metallic_roughness.setdefault("roughnessFactor", 1.0)
                                 mat_updated_internally = True
                         # Normal Check
                         tex_info = mat.get("normalTexture")
                         tex_index = tex_info.get("index") if tex_info else None
                         if tex_index is not None and 0 <= tex_index < len(textures):
                              source_img_index = textures[tex_index].get("source")
                              if source_img_index in image_uri_map: mat_updated_internally = True

                         if mat_updated_internally:
                             mat["pbrMetallicRoughness"] = pbr_metallic_roughness
                             extensions.pop("KHR_materials_pbrSpecularGlossiness")
                             if not extensions: mat.pop("extensions", None)
                             removed_specgloss_extension = True
                             logger.info(f"    Material '{mat_name}' [{mat_index}]: Converted to PBR MetRough.")
                     else: # Standard PBR check
                         keys_to_check = ["baseColorTexture", "metallicRoughnessTexture", "normalTexture", "occlusionTexture", "emissiveTexture"]
                         locs = [mat, pbr_metallic_roughness]
                         updated = False
                         for key in keys_to_check:
                              for loc in locs:
                                   tex_info = loc.get(key)
                                   if tex_info and isinstance(tex_info, dict):
                                       idx = tex_info.get("index")
                                       if idx is not None and 0 <= idx < len(textures):
                                            src_idx = textures[idx].get("source")
                                            if src_idx in image_uri_map: mat_updated_internally = True; updated=True; break
                              if updated: break

                     if mat_updated_internally: file_updated = True

                 if file_updated:
                     if removed_specgloss_extension:
                         if "KHR_materials_pbrSpecularGlossiness" in used_extensions:
                             used_extensions.remove("KHR_materials_pbrSpecularGlossiness")
                             if used_extensions: gltf_data["extensionsUsed"] = sorted(list(used_extensions))
                             else: gltf_data.pop("extensionsUsed", None)
                         if "KHR_materials_pbrSpecularGlossiness" in required_extensions:
                              required_extensions.remove("KHR_materials_pbrSpecularGlossiness")
                              if required_extensions: gltf_data["extensionsRequired"] = sorted(list(required_extensions))
                              else: gltf_data.pop("extensionsRequired", None)
                     output_gltf_path = gltf_path
                     logger.info(f"  Saving updated GLTF file to: {output_gltf_path}")
                     try:
                         with open(output_gltf_path, "w", encoding='utf-8') as f:
                             json.dump(gltf_data, f, indent=2, ensure_ascii=False, separators=(',', ': '))
                         updated_gltf_count += 1
                     except Exception as e: logger.error(f"  Error writing updated GLTF: {e}")
                 else: logger.info(f"  No relevant changes in {entry.name}.")

    except OSError as e: logger.error(f"GLTF Update: Error scanning {input_folder}: {e}")
    except Exception as e: logger.error(f"GLTF Update: Unexpected error: {e}", exc_info=True)

    if gltf_found_count == 0: logger.info("GLTF Update: No .gltf files found.")
    else: logger.info(f"GLTF Update: Processed {gltf_found_count} GLTF files, updated {updated_gltf_count}.")