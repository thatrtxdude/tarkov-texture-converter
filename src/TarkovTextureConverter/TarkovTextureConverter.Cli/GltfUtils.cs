// GltfUtils.cs
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TarkovTextureConverter.Cli;

public static class GltfUtils
{
    public static async Task UpdateGltfFilesAsync(string inputFolder, string outputFolderAbsPath, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(inputFolder))
        {
            logger.LogError("GLTF Update: Input folder '{InputFolder}' not found.", inputFolder);
            return;
        }

        string outputFolderBasename = Path.GetFileName(outputFolderAbsPath);
        logger.LogInformation("Scanning for GLTF files in '{InputFolder}' to update URIs relative to '{OutputFolderBasename}'...", inputFolder, outputFolderBasename);

        int gltfFoundCount = 0;
        int updatedGltfCount = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(inputFolder, "*.gltf", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                gltfFoundCount++;
                logger.LogInformation("Processing GLTF file: {FilePath}", filePath);
                bool fileUpdated = false;

                try
                {
                    string jsonContent = await File.ReadAllTextAsync(filePath, cancellationToken);

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

                    var updatedImageUriMap = new Dictionary<int, string>();

                    for (int i = 0; i < images.Count; i++)
                    {
                         JsonObject? img = images[i]?.AsObject();
                         if (img == null || !img.TryGetPropertyValue("uri", out JsonNode? uriNode) || uriNode == null) continue;

                         string? oldUri = uriNode.GetValue<string>();
                         if (string.IsNullOrEmpty(oldUri) || oldUri.StartsWith("data:")) continue;

                         string oldFilename = Path.GetFileName(oldUri.Replace("\\", "/"));
                         TextureType? texType = TextureProcessor.GetTextureType(oldFilename, tarkinMode: true);

                         string? newUriBase = texType switch
                         {
                             TextureType.Diffuse => Utils.InsertSuffix(oldFilename, "_color"),
                             TextureType.SpecGlos => Utils.InsertSuffix(oldFilename, "_roughness"),
                             TextureType.Normal => Utils.InsertSuffix(oldFilename, "_converted"),
                             _ => null
                         };

                         if (!string.IsNullOrEmpty(newUriBase))
                         {
                            newUriBase = Path.ChangeExtension(newUriBase, ".png");
                            string newUri = Path.Combine(outputFolderBasename, newUriBase).Replace("\\", "/");

                             if (img["uri"]?.GetValue<string>() != newUri)
                             {
                                 logger.LogInformation("    Image [{ImageIndex}]: Updating URI '{OldUri}' -> '{NewUri}'", i, oldUri, newUri);
                                 img["uri"] = newUri;
                                 updatedImageUriMap[i] = newUri;
                                 fileUpdated = true;
                             }
                         }
                    }

                    bool specGlossExtensionRemoved = false;
                    if (fileUpdated && materials != null)
                    {
                         for(int matIdx = 0; matIdx < materials.Count; matIdx++)
                         {
                             JsonObject? mat = materials[matIdx]?.AsObject();
                             if (mat == null) continue;
                             string matName = mat["name"]?.GetValue<string>() ?? $"Material_{matIdx}";
                             bool matModified = false;

                             if (mat.TryGetPropertyValue("extensions", out JsonNode? extNode) &&
                                 extNode is JsonObject extensions &&
                                 extensions.TryGetPropertyValue("KHR_materials_pbrSpecularGlossiness", out JsonNode? specGlossNode) &&
                                 specGlossNode is JsonObject specGloss)
                             {
                                  logger.LogInformation("    Material '{MatName}' [{MatIndex}]: Found KHR_materials_pbrSpecularGlossiness.", matName, matIdx);
                                  JsonObject pbrMetallicRoughness = mat["pbrMetallicRoughness"]?.AsObject() ?? new JsonObject();

                                  if (specGloss.TryGetPropertyValue("diffuseTexture", out JsonNode? diffTexInfoNode))
                                  {
                                       int? texIndex = diffTexInfoNode?["index"]?.GetValue<int>();
                                       int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                       if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                       {
                                            pbrMetallicRoughness["baseColorTexture"] = diffTexInfoNode.DeepClone();
                                            logger.LogDebug("      Mapped diffuseTexture -> baseColorTexture");
                                            matModified = true;
                                       }
                                  }
                                   if (specGloss.TryGetPropertyValue("diffuseFactor", out JsonNode? diffFactorNode))
                                   {
                                       pbrMetallicRoughness["baseColorFactor"] = diffFactorNode.DeepClone();
                                       matModified = true;
                                   }

                                  if (specGloss.TryGetPropertyValue("specularGlossinessTexture", out JsonNode? sgTexInfoNode))
                                  {
                                       int? texIndex = sgTexInfoNode?["index"]?.GetValue<int>();
                                       int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                       if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                       {
                                            pbrMetallicRoughness["metallicRoughnessTexture"] = sgTexInfoNode.DeepClone();
                                             pbrMetallicRoughness["metallicFactor"] ??= 0.0;
                                             pbrMetallicRoughness["roughnessFactor"] ??= 1.0;
                                             logger.LogDebug("      Mapped specularGlossinessTexture -> metallicRoughnessTexture (using generated _roughness map)");
                                             matModified = true;
                                       }
                                  }
                                   if (matModified)
                                   {
                                        pbrMetallicRoughness["metallicFactor"] ??= 0.0;
                                        pbrMetallicRoughness["roughnessFactor"] ??= 1.0;
                                   }

                                   if (!matModified && mat.TryGetPropertyValue("normalTexture", out JsonNode? normTexInfoNode))
                                   {
                                        int? texIndex = normTexInfoNode?["index"]?.GetValue<int>();
                                        int? sourceImgIndex = GetSourceImageIndex(textures, texIndex);
                                        if (sourceImgIndex.HasValue && updatedImageUriMap.ContainsKey(sourceImgIndex.Value))
                                        {
                                             matModified = true;
                                             logger.LogDebug("      Normal map URI was updated.");
                                        }
                                   }

                                  if (matModified)
                                  {
                                       mat["pbrMetallicRoughness"] = pbrMetallicRoughness;
                                       extensions.Remove("KHR_materials_pbrSpecularGlossiness");
                                       specGlossExtensionRemoved = true;
                                       if (extensions.Count == 0) mat.Remove("extensions");
                                       logger.LogInformation("    Material '{MatName}' [{MatIndex}]: Converted to PBR MetallicRoughness.", matName, matIdx);
                                  }
                             }
                             else
                             {
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
                                           break;
                                       }
                                  }
                             }
                             if(matModified) fileUpdated = true;

                         }
                    }

                    if (specGlossExtensionRemoved)
                    {
                        RemoveExtension(rootNode, "extensionsUsed", "KHR_materials_pbrSpecularGlossiness");
                        RemoveExtension(rootNode, "extensionsRequired", "KHR_materials_pbrSpecularGlossiness");
                    }

                    if (fileUpdated)
                    {
                        // Save updated GLTF file with "_converted" suffix instead of overwriting the original.
                        string originalFileName = Path.GetFileName(filePath);
                        string newFileName = Utils.InsertSuffix(originalFileName, "_converted");
                        string outputGltfPath = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, newFileName);

                        logger.LogInformation("  Saving updated GLTF file to: {OutputGltfPath}", outputGltfPath);
                        try
                        {
                            var writeOptions = new JsonSerializerOptions
                            {
                                WriteIndented = true,
                                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
                            };
                            await File.WriteAllTextAsync(outputGltfPath, rootNode.ToJsonString(writeOptions), cancellationToken);
                            updatedGltfCount++;
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogWarning("  Writing updated GLTF file cancelled: {OutputGltfPath}", outputGltfPath);
                            throw;
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
                catch (OperationCanceledException)
                {
                    logger.LogWarning("GLTF processing cancelled for file: {FilePath}", filePath);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "  Unexpected error processing GLTF file {FilePath}", filePath);
                }
            }
        }
        catch (OperationCanceledException)
        {
             logger.LogWarning("GLTF file scanning/processing was cancelled.");
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

    private static int? GetSourceImageIndex(JsonArray? textures, int? textureIndex)
    {
        if (textures == null || !textureIndex.HasValue || textureIndex.Value < 0 || textureIndex.Value >= textures.Count)
            return null;
        return textures[textureIndex.Value]?["source"]?.GetValue<int>();
    }

    private static void RemoveExtension(JsonNode root, string arrayName, string extensionToRemove)
    {
         if (root[arrayName] is JsonArray extensionsArray)
         {
            JsonNode? toRemove = extensionsArray.ToList().FirstOrDefault(node => node?.GetValue<string>() == extensionToRemove);
            if (toRemove != null)
            {
                extensionsArray.Remove(toRemove);
                if (extensionsArray.Count == 0)
                {
                    if(root.AsObject().TryGetPropertyValue(arrayName, out _))
                    {
                         root.AsObject().Remove(arrayName);
                    }
                }
            }
         }
    }
}