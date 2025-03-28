using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes; // Requires .NET 6+ for mutable JSON DOM


namespace TarkovTextureConverter.Cli;

public static class GltfUtils
{
    public static void UpdateGltfFiles(string inputFolder, string outputFolderAbsPath, ILogger logger)
    {
        if (!Directory.Exists(inputFolder))
        {
            logger.LogError("GLTF Update: Input folder '{InputFolder}' not found.", inputFolder);
            return;
        }

        string outputFolderBasename = Path.GetFileName(outputFolderAbsPath); // e.g., "converted_textures" or "converted_textures_1"
        logger.LogInformation("Scanning for GLTF files in '{InputFolder}' to update URIs relative to '{OutputFolderBasename}'...", inputFolder, outputFolderBasename);

        int gltfFoundCount = 0;
        int updatedGltfCount = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(inputFolder, "*.gltf", SearchOption.TopDirectoryOnly))
            {
                gltfFoundCount++;
                logger.LogInformation("Processing GLTF file: {FilePath}", filePath);
                bool fileUpdated = false;

                try
                {
                    string jsonContent = File.ReadAllText(filePath);
                    // Use JsonNode for easier modification (.NET 6+)
                    JsonNode? rootNode = JsonNode.Parse(jsonContent);
                    if (rootNode == null)
                    {
                        logger.LogError("  Failed to parse GLTF file: {FilePath}", filePath);
                        continue;
                    }

                    JsonArray? images = rootNode["images"]?.AsArray();
                    JsonArray? textures = rootNode["textures"]?.AsArray();
                    JsonArray? materials = rootNode["materials"]?.AsArray();

                    if (images == null || textures == null)
                    {
                        logger.LogInformation("  Skipping GLTF file: No 'images' or 'textures' array found.");
                        continue;
                    }

                    // Map image index to its new URI if updated
                    var updatedImageUriMap = new Dictionary<int, string>();

                    for (int i = 0; i < images.Count; i++)
                    {
                         JsonObject? img = images[i]?.AsObject();
                         if (img == null || !img.TryGetPropertyValue("uri", out JsonNode? uriNode) || uriNode == null) continue;

                         string? oldUri = uriNode.GetValue<string>();
                         if (string.IsNullOrEmpty(oldUri) || oldUri.StartsWith("data:")) continue;

                         string oldFilename = Path.GetFileName(oldUri.Replace("\\", "/")); // Normalize slashes and get filename
                         TextureType? texType = TextureProcessor.GetTextureType(oldFilename, tarkinMode: true); // Always Tarkin logic here

                         string? newUriBase = texType switch
                         {
                             TextureType.Diffuse => Utils.InsertSuffix(oldFilename, "_color"),
                             TextureType.SpecGlos => Utils.InsertSuffix(oldFilename, "_roughness"), // Tarkin uses Roughness from SpecGlos for MetallicRoughnessTexture
                             TextureType.Normal => Utils.InsertSuffix(oldFilename, "_converted"),
                             _ => null
                         };

                         // Ensure the new base has a .png extension (SaveImage forces it)
                         if (!string.IsNullOrEmpty(newUriBase))
                         {
                            newUriBase = Path.ChangeExtension(newUriBase, ".png");
                            // Combine with relative output folder path, use forward slashes for GLTF URI
                            string newUri = Path.Combine(outputFolderBasename, newUriBase).Replace("\\", "/");

                             if (img["uri"]?.GetValue<string>() != newUri)
                             {
                                 logger.LogInformation("    Image [{ImageIndex}]: Updating URI '{OldUri}' -> '{NewUri}'", i, oldUri, newUri);
                                 img["uri"] = newUri; // Modify the JsonNode
                                 updatedImageUriMap[i] = newUri;
                                 fileUpdated = true;
                             }
                         }
                    } // End image loop

                    // Process materials if any images were updated
                    bool specGlossExtensionRemoved = false;
                    if (fileUpdated && materials != null)
                    {
                         for(int matIdx = 0; matIdx < materials.Count; matIdx++)
                         {
                             JsonObject? mat = materials[matIdx]?.AsObject();
                             if (mat == null) continue;

                             string matName = mat["name"]?.GetValue<string>() ?? $"Material_{matIdx}";
                             bool matModified = false;

                             // Check for KHR_materials_pbrSpecularGlossiness and convert
                             if (mat.TryGetPropertyValue("extensions", out JsonNode? extNode) &&
                                 extNode is JsonObject extensions &&
                                 extensions.TryGetPropertyValue("KHR_materials_pbrSpecularGlossiness", out JsonNode? specGlossNode) &&
                                 specGlossNode is JsonObject specGloss)
                             {
                                  logger.LogInformation("    Material '{MatName}' [{MatIndex}]: Found KHR_materials_pbrSpecularGlossiness.", matName, matIdx);
                                  JsonObject pbrMetallicRoughness = mat["pbrMetallicRoughness"]?.AsObject() ?? new JsonObject();

                                  // Diffuse -> BaseColorTexture
                                  if (specGloss.TryGetPropertyValue("diffuseTexture", out JsonNode? diffTexInfoNode))
                                  {
                                       int? texIndex = diffTexInfoNode?["index"]?.GetValue<int>();
                                       int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                       if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                       {
                                            pbrMetallicRoughness["baseColorTexture"] = diffTexInfoNode.DeepClone(); // Clone node
                                            logger.LogDebug("      Mapped diffuseTexture -> baseColorTexture");
                                            matModified = true;
                                       }
                                  }
                                   if (specGloss.TryGetPropertyValue("diffuseFactor", out JsonNode? diffFactorNode))
                                   {
                                       pbrMetallicRoughness["baseColorFactor"] = diffFactorNode.DeepClone();
                                       matModified = true;
                                   }

                                  // SpecularGlossinessTexture -> MetallicRoughnessTexture
                                  // NOTE: Tarkin conversion generates a Roughness map from the Gloss (alpha).
                                  // We updated the *original* specGlos image URI to point to this new _roughness.png.
                                  if (specGloss.TryGetPropertyValue("specularGlossinessTexture", out JsonNode? sgTexInfoNode))
                                  {
                                       int? texIndex = sgTexInfoNode?["index"]?.GetValue<int>();
                                       int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                       if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                       {
                                            // The URI for this texture now points to _roughness.png
                                            pbrMetallicRoughness["metallicRoughnessTexture"] = sgTexInfoNode.DeepClone();
                                             // Standard PBR defaults when converting from Spec/Gloss
                                             pbrMetallicRoughness["metallicFactor"] ??= 0.0; // Use ??= to set only if null
                                             pbrMetallicRoughness["roughnessFactor"] ??= 1.0;
                                             logger.LogDebug("      Mapped specularGlossinessTexture -> metallicRoughnessTexture (using generated _roughness map)");
                                             matModified = true;
                                       }
                                  }
                                   // Add default factors if texture wasn't present but SpecGloss ext was
                                   if (matModified)
                                   {
                                        pbrMetallicRoughness["metallicFactor"] ??= 0.0;
                                        pbrMetallicRoughness["roughnessFactor"] ??= 1.0;
                                   }

                                  // Normal texture - just check if its source image was updated
                                   if (!matModified && mat.TryGetPropertyValue("normalTexture", out JsonNode? normTexInfoNode))
                                   {
                                        int? texIndex = normTexInfoNode?["index"]?.GetValue<int>();
                                        int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                        if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                        {
                                             matModified = true; // Mark material as modified even if only normal map changed URI
                                             logger.LogDebug("      Normal map URI was updated.");
                                        }
                                   }


                                  if (matModified)
                                  {
                                       mat["pbrMetallicRoughness"] = pbrMetallicRoughness; // Assign potentially new/modified object
                                       extensions.Remove("KHR_materials_pbrSpecularGlossiness");
                                       specGlossExtensionRemoved = true;
                                       if (extensions.Count == 0) mat.Remove("extensions");
                                       logger.LogInformation("    Material '{MatName}' [{MatIndex}]: Converted to PBR MetallicRoughness.", matName, matIdx);
                                  }
                             }
                             else // Check standard PBR materials if their textures were updated
                             {
                                  string[] pbrTextureKeys = { "baseColorTexture", "metallicRoughnessTexture" }; // Add others like occlusionTexture if needed
                                  JsonObject? pbr = mat["pbrMetallicRoughness"]?.AsObject();
                                  JsonNode?[] textureNodes = {
                                       mat["normalTexture"], mat["occlusionTexture"], mat["emissiveTexture"],
                                       pbr?["baseColorTexture"], pbr?["metallicRoughnessTexture"]
                                  };

                                  foreach(var texInfoNode in textureNodes)
                                  {
                                      if (texInfoNode == null) continue;
                                       int? texIndex = texInfoNode["index"]?.GetValue<int>();
                                       int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                       if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                       {
                                           matModified = true;
                                           logger.LogDebug("    Material '{MatName}' [{MatIndex}]: Referenced image URI was updated.", matName, matIdx);
                                           break; // Found one updated reference, no need to check others for this mat
                                       }
                                  }
                             }
                             if(matModified) fileUpdated = true; // Ensure file is marked updated if any material changed

                         } // End material loop
                    } // End if(fileUpdated && materials != null)

                    // Update global extensions lists if SpecGloss was removed
                    if (specGlossExtensionRemoved)
                    {
                        RemoveExtension(rootNode, "extensionsUsed", "KHR_materials_pbrSpecularGlossiness");
                        RemoveExtension(rootNode, "extensionsRequired", "KHR_materials_pbrSpecularGlossiness");
                    }

                    // Save if changes were made
                    if (fileUpdated)
                    {
                        string outputGltfPath = filePath; // Overwrite original
                        logger.LogInformation("  Saving updated GLTF file to: {OutputGltfPath}", outputGltfPath);
                        try
                        {
                            var writeOptions = new JsonSerializerOptions
                            {
                                WriteIndented = true,
                                // Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // If needed for paths/special chars
                                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) // Safer for non-ASCII chars if present
                            };
                            File.WriteAllText(outputGltfPath, rootNode.ToJsonString(writeOptions));
                            updatedGltfCount++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "  Error writing updated GLTF: {OutputGltfPath}", outputGltfPath);
                        }
                    }
                    else
                    {
                        logger.LogInformation("  No relevant texture URIs or material structures needed updating in {FileName}.", Path.GetFileName(filePath));
                    }
                }
                catch (JsonException jsonEx)
                {
                    logger.LogError(jsonEx, "  Error processing GLTF file {FilePath}", filePath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "  Unexpected error processing GLTF file {FilePath}", filePath);
                }
            } // End file loop
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            logger.LogError(ex, "GLTF Update: Error scanning directory {InputFolder}", inputFolder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GLTF Update: Unexpected error during scan");
        }

        if (gltfFoundCount == 0)
        {
            logger.LogInformation("GLTF Update: No .gltf files found in {InputFolder}.", inputFolder);
        }
        else
        {
            logger.LogInformation("GLTF Update: Finished processing {GltfFoundCount} GLTF files. Updated {UpdatedGltfCount} files.", gltfFoundCount, updatedGltfCount);
        }
    }

    // Helper to get the source image index from a texture index
    private static int? GetSourceImageIndex(JsonArray textures, int? textureIndex)
    {
        if (!textureIndex.HasValue || textureIndex.Value < 0 || textureIndex.Value >= textures.Count)
            return null;
        return textures[textureIndex.Value]?["source"]?.GetValue<int>();
    }

     // Helper to remove an extension string from extensionsUsed/Required arrays
    private static void RemoveExtension(JsonNode root, string arrayName, string extensionToRemove)
    {
         if (root[arrayName] is JsonArray extensionsArray)
         {
            JsonNode? toRemove = extensionsArray.FirstOrDefault(node => node?.GetValue<string>() == extensionToRemove);
            if (toRemove != null)
            {
                extensionsArray.Remove(toRemove);
                if (extensionsArray.Count == 0)
                {
                    root.AsObject().Remove(arrayName); // Remove empty array property
                }
            }
         }
    }
}