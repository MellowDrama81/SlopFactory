using System.Text.Json;

namespace Mellow.SlopFactory.Domain;

/// <summary>One entry in the built-in ComfyUI workflow library — see <see cref="ComfyBuiltInWorkflows"/>.</summary>
public sealed record ComfyBuiltInWorkflow(
    string Id,
    string Name,
    string Description,
    int ReferenceImageCount,
    string WorkflowTemplate,
    IReadOnlyList<ComfyWorkflowTuningParameter>? TuningParameters = null)
{
    /// <summary>Node types and model filenames detected from the workflow's own API-format graph.
    /// This is descriptive rather than a Cloud availability guarantee: Cloud's experimental node
    /// inventory can omit nodes that an active worker nevertheless executes.</summary>
    public ComfyWorkflowRequirements Requirements { get; } = ComfyWorkflowRequirements.FromTemplate(WorkflowTemplate);

    /// <summary>Small, safe defaults worth preserving when a user customizes a ControlNet graph.</summary>
    public IReadOnlyList<ComfyWorkflowTuningParameter> Tuning { get; } = TuningParameters ?? [];
}

/// <summary>A documented default embedded in a built-in workflow rather than a runtime override.</summary>
public sealed record ComfyWorkflowTuningParameter(string Name, string DefaultValue, string Effect);

/// <summary>Declared dependencies extracted from a built-in workflow's API-format JSON.</summary>
public sealed record ComfyWorkflowRequirements(IReadOnlyList<string> NodeTypes, IReadOnlyList<string> ModelFiles)
{
    private static readonly HashSet<string> ModelInputNames = new(StringComparer.Ordinal)
    {
        "ckpt_name", "clip_name", "control_net_name", "lora_name", "unet_name", "vae_name"
    };

    public static ComfyWorkflowRequirements FromTemplate(string template)
    {
        var normalized = template.Replace("{{SEED}}", "0", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(normalized);
        var nodeTypes = new SortedSet<string>(StringComparer.Ordinal);
        var modelFiles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in document.RootElement.EnumerateObject())
        {
            if (node.Value.TryGetProperty("class_type", out var classType) && classType.ValueKind == JsonValueKind.String && classType.GetString() is { Length: > 0 } type)
                nodeTypes.Add(type);
            if (!node.Value.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
            foreach (var input in inputs.EnumerateObject())
            {
                if (ModelInputNames.Contains(input.Name) && input.Value.ValueKind == JsonValueKind.String && input.Value.GetString() is { Length: > 0 } filename)
                    modelFiles.Add(filename);
            }
        }

        return new ComfyWorkflowRequirements(nodeTypes.ToArray(), modelFiles.ToArray());
    }
}

/// <summary>
/// A small library of ready-to-use ComfyUI API-format workflow templates for
/// <see cref="Model.ComfyWorkflowTemplate"/>, so a user doesn't have to hand-export and placeholder-tag
/// their own workflow just to get started. Every template here was supplied by the user already in
/// ComfyUI's API-format shape (<c>{"&lt;node id&gt;":{"class_type","inputs"}}</c>) — this class only adds
/// the <c>{{PROMPT}}</c>/<c>{{SEED}}</c>/<c>{{UPLOADED_IMAGE_FILENAME}}</c>[<c>_2</c>] placeholder tokens
/// (see <c>ComfyUiProviderAdapter.SubstitutePlaceholders</c>) at the node/field each workflow's own
/// prompt, seed, and reference-image slot(s) actually live at — every other field (checkpoint/LoRA
/// names, samplers, steps, negative prompts) is left exactly as supplied. None of these were
/// independently re-verified against a live Comfy Cloud account by this app's own testing (the only
/// workflow that was — a minimal SD1.5 graph — predates this library and isn't part of it); they are
/// included on the basis of the user's own working exports.
/// <para>
/// Two entries (<c>ByteDanceSeedreamNode</c>/<c>GeminiImage2Node</c>-based) call ComfyUI's built-in "API
/// Nodes," which proxy to a third-party hosted model (ByteDance Seedream, Google's Gemini image model)
/// rather than running on local/Cloud GPU compute — these require that third-party integration to be
/// separately authorized/credited on the user's own Comfy account, on top of the Comfy Cloud API key
/// this app's <c>Connection</c> already uses. Every other entry runs entirely against locally-installed
/// (Cloud-hosted) checkpoints.
/// </para>
/// </summary>
public static class ComfyBuiltInWorkflows
{
    private static readonly IReadOnlyList<ComfyWorkflowTuningParameter> QwenUnionControlTuning =
    [
        new("Control strength", "1.0", "Keeps the output tightly aligned to the supplied guide. Lower it only when the guide is overpowering the prompt."),
        new("Control schedule", "start 0.0, end 1.0", "Applies guide influence across the whole denoising pass; preserve this full range as the baseline."),
        new("Sampling", "20 steps, CFG 4.0", "Balances speed and prompt adherence for the direct guide workflow.")
    ];

    private static readonly IReadOnlyList<ComfyWorkflowTuningParameter> QwenDWPoseUnionControlTuning =
    [
        new("Control strength", "1.0", "Keeps the generated composition and body pose strongly aligned to the extracted DWPose guide."),
        new("Control schedule", "start 0.0, end 1.0", "Carries pose guidance through the whole denoising pass; preserve this full range as the baseline."),
        new("Sampling", "24 steps, CFG 4.0", "Allows a little more refinement after pose extraction while retaining the existing prompt balance.")
    ];

    private static readonly IReadOnlyList<ComfyWorkflowTuningParameter> QwenInpaintControlTuning =
    [
        new("Control strength", "1.0", "Keeps the replacement closely constrained by the masked source image."),
        new("Control schedule", "start 0.0, end 1.0", "Uses the inpainting control throughout the denoising pass; preserve this full range as the baseline."),
        new("Masked-region sampling", "20 steps, CFG 2.5, denoise 1.0", "Fully regenerates only the masked area while the workflow composites unmasked pixels back unchanged.")
    ];

    public static readonly IReadOnlyList<ComfyBuiltInWorkflow> All =
    [
        new ComfyBuiltInWorkflow(
            "z-image-turbo",
            "Z-Image Turbo — text to image",
            "Fast few-step text-to-image with Z-Image Turbo (Lumina/AuraFlow-based). No reference image.",
            ReferenceImageCount: 0,
            WorkflowTemplate: ZImageTurbo),
        new ComfyBuiltInWorkflow(
            "flux2",
            "FLUX.2 — text to image (with optional style reference)",
            "FLUX.2 dev text-to-image with an optional 8-step LoRA toggle baked into the graph. Accepts one reference image (used as a style/composition guide, not a strict edit).",
            ReferenceImageCount: 1,
            WorkflowTemplate: Flux2),
        new ComfyBuiltInWorkflow(
            "flux2-klein-edit-single",
            "FLUX.2 Klein 9B — single-image edit",
            "Edits one reference image per the prompt using FLUX.2 Klein's base 9B model.",
            ReferenceImageCount: 1,
            WorkflowTemplate: Flux2KleinEditSingle),
        new ComfyBuiltInWorkflow(
            "flux2-klein-edit-double",
            "FLUX.2 Klein 9B — dual-image edit",
            "Combines two reference images per the prompt (e.g. \"apply this logo from image 2 onto the object in image 1\") using FLUX.2 Klein's base 9B model.",
            ReferenceImageCount: 2,
            WorkflowTemplate: Flux2KleinEditDouble),
        new ComfyBuiltInWorkflow(
            "flux2-klein-inpaint-reference",
            "FLUX.2 Klein 9B — masked reference inpainting",
            "Uses image 1 as the masked inpainting base and image 2 as a visual reference. Only the selected private-mask area is noised, and the generated result is composited over image 1 so every unmasked pixel is preserved.",
            ReferenceImageCount: 2,
            WorkflowTemplate: Flux2KleinInpaintReference),
        new ComfyBuiltInWorkflow(
            "krea2-style-reference",
            "Krea 2 Turbo — image style reference",
            "Generates a new image guided by the prompt and the style of one reference image, using Krea 2 Turbo's dedicated style-reference LoRA. Includes an optional (disabled by default) LLM prompt-expansion step baked into the graph.",
            ReferenceImageCount: 1,
            WorkflowTemplate: Krea2StyleReference),
        new ComfyBuiltInWorkflow(
            "netayume-lumina-t2i",
            "NetaYume Lumina 3.5 — anime text to image",
            "Anime-style text-to-image with NetaYume Lumina 3.5. No reference image.",
            ReferenceImageCount: 0,
            WorkflowTemplate: NetaYumeLuminaT2I),
        new ComfyBuiltInWorkflow(
            "newbieimage-exp0-1-t2i",
            "NewBie-Image Exp 0.1 — tag-based anime text to image",
            "Anime text-to-image with NewBie-Image Exp 0.1. Expects a Danbooru-style tag list (comma-separated tags, e.g. \"1girl, blue hair, forest, dramatic lighting\") rather than a natural-language sentence — the prompt is substituted directly into the model's expected tag-structured template. No reference image.",
            ReferenceImageCount: 0,
            WorkflowTemplate: NewbieImageExp01T2I),
        new ComfyBuiltInWorkflow(
            "qwen-image-edit-2511",
            "Qwen-Image-Edit 2511 — dual-image edit",
            "Combines two reference images per the prompt (e.g. transferring a material or attribute from one image onto the other) using Qwen-Image-Edit 2511, with an optional (disabled by default) 4-step LoRA toggle baked into the graph.",
            ReferenceImageCount: 2,
            WorkflowTemplate: QwenImageEdit2511),
        new ComfyBuiltInWorkflow(
            "qwen-image-edit-2511-inpainting",
            "Qwen-Image-Edit 2511 — masked dual-image inpainting",
            "Uses image 1 as the inpaint base and image 2 as a reference. Select a private mask for image 1: the app encodes its painted area as transparency for ComfyUI, the workflow edits that region, then composites the result back over the original to preserve every unmasked pixel.",
            ReferenceImageCount: 2,
            WorkflowTemplate: QwenImageEdit2511Inpainting),
        new ComfyBuiltInWorkflow(
            "qwen-instantx-union-controlnet",
            "Qwen Image + InstantX Union ControlNet",
            "Generates an image under strong control of one supplied guide image using Qwen Image and InstantX Union ControlNet. Use a prepared control image such as a depth map, Canny edge map, or another Union-compatible guide; it is not an image-reference/edit workflow.",
            ReferenceImageCount: 1,
            WorkflowTemplate: QwenInstantXUnionControlNet,
            TuningParameters: QwenUnionControlTuning),
        new ComfyBuiltInWorkflow(
            "dwpose-extract-pose-guide",
            "DWPose — extract a pose guide",
            "Extracts a reusable OpenPose-style skeleton from one supplied image, including body, hands, and face landmarks. Use its saved image as the guide input for a pose-compatible ControlNet workflow such as Qwen Image + InstantX Union ControlNet.",
            ReferenceImageCount: 1,
            WorkflowTemplate: DWPoseExtractPoseGuide),
        new ComfyBuiltInWorkflow(
            "canny-extract-control-guide",
            "Canny — extract an edge guide",
            "Extracts a high-contrast Canny edge map from one supplied image. Use it as a precise composition or edge-control guide in a compatible ControlNet workflow.",
            ReferenceImageCount: 1,
            WorkflowTemplate: CannyExtractControlGuide),
        new ComfyBuiltInWorkflow(
            "lineart-extract-control-guide",
            "Lineart — extract a contour guide",
            "Extracts clean lineart from one supplied image while reducing texture. Use it as a structural guide for redraw, stylization, or a compatible ControlNet workflow.",
            ReferenceImageCount: 1,
            WorkflowTemplate: LineartExtractControlGuide),
        new ComfyBuiltInWorkflow(
            "depth-extract-control-guide",
            "Depth Anything V2 — extract a depth guide",
            "Extracts a relative depth map from one supplied image using Depth Anything V2. Use it as a spatial-structure guide in a compatible ControlNet workflow.",
            ReferenceImageCount: 1,
            WorkflowTemplate: DepthExtractControlGuide),
        new ComfyBuiltInWorkflow(
            "normal-extract-control-guide",
            "BAE — extract a normal-map guide",
            "Extracts a surface-normal map from one supplied image using BAE. Use it for relighting, material-aware stylization, or a compatible normal-control workflow.",
            ReferenceImageCount: 1,
            WorkflowTemplate: NormalExtractControlGuide),
        new ComfyBuiltInWorkflow(
            "qwen-dwpose-union-controlnet",
            "Qwen Image — generate from a photo's pose",
            "Extracts the body, hands, and face pose from one supplied image with DWPose, then generates a new image under that pose using Qwen Image + InstantX Union ControlNet. This uses the photo as a pose source only, not as an identity or style reference.",
            ReferenceImageCount: 1,
            WorkflowTemplate: QwenDWPoseUnionControlNet,
            TuningParameters: QwenDWPoseUnionControlTuning),
        new ComfyBuiltInWorkflow(
            "qwen-image-instantx-inpainting",
            "Qwen-Image + InstantX ControlNet — mask inpainting",
            "Inpaints part of one reference image per the prompt using Qwen-Image with the InstantX inpainting ControlNet. The masked region comes from the uploaded image's own alpha channel — export/upload an image with the area to change made transparent.",
            ReferenceImageCount: 1,
            WorkflowTemplate: QwenImageInstantXInpainting,
            TuningParameters: QwenInpaintControlTuning),
        new ComfyBuiltInWorkflow(
            "api-bytedance-seedream4",
            "ByteDance Seedream 4.5/5.0 (API Node) — image edit",
            "Edits one reference image per the prompt using ByteDance's hosted Seedream model via ComfyUI's API Nodes integration. Requires Seedream access separately authorized/credited on your Comfy account, on top of your Comfy Cloud API key.",
            ReferenceImageCount: 1,
            WorkflowTemplate: ApiBytedanceSeedream4),
        new ComfyBuiltInWorkflow(
            "api-nano-banana-pro",
            "Nano Banana Pro / Gemini Image (API Node) — dual-image edit",
            "Combines two reference images per the prompt using Google's Gemini image model via ComfyUI's API Nodes integration. Requires Gemini image access separately authorized/credited on your Comfy account, on top of your Comfy Cloud API key.",
            ReferenceImageCount: 2,
            WorkflowTemplate: ApiNanoBananaPro),
    ];

    /// <summary>Identifies a workflow that accepts an already-prepared control guide directly.
    /// A graph which first runs DWPose (or another preprocessor) is intentionally excluded: passing
    /// it an extracted guide would make it preprocess the guide a second time. Custom workflows may
    /// opt in by using the same direct Union-ControlNet shape as the built-in target.</summary>
    public static bool IsDirectControlGuideTarget(string? workflowTemplate)
    {
        if (string.IsNullOrWhiteSpace(workflowTemplate)) return false;

        try
        {
            using var document = JsonDocument.Parse(workflowTemplate.Replace("{{SEED}}", "0", StringComparison.Ordinal));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            var classTypes = new HashSet<string>(StringComparer.Ordinal);
            var hasUnionControlNet = false;
            foreach (var node in document.RootElement.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("class_type", out var classType) || classType.ValueKind != JsonValueKind.String) continue;
                var type = classType.GetString();
                if (string.IsNullOrWhiteSpace(type)) continue;
                classTypes.Add(type);

                if (type == "ControlNetLoader" &&
                    node.Value.TryGetProperty("inputs", out var inputs) &&
                    inputs.TryGetProperty("control_net_name", out var modelName) &&
                    modelName.ValueKind == JsonValueKind.String &&
                    modelName.GetString()?.Contains("union", StringComparison.OrdinalIgnoreCase) == true)
                {
                    hasUnionControlNet = true;
                }
            }

            return hasUnionControlNet &&
                   classTypes.Contains("ControlNetApplyAdvanced") &&
                   !classTypes.Contains("DWPreprocessor");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private const string ZImageTurbo = """
        {
          "9": {
            "inputs": {
              "filename_prefix": "z-image-turbo",
              "images": [
                "57:8",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "57:30": {
            "inputs": {
              "clip_name": "qwen_3_4b.safetensors",
              "type": "lumina2",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "57:29": {
            "inputs": {
              "vae_name": "ae.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "57:33": {
            "inputs": {
              "conditioning": [
                "57:27",
                0
              ]
            },
            "class_type": "ConditioningZeroOut",
            "_meta": {
              "title": "Conditioning Zero Out"
            }
          },
          "57:8": {
            "inputs": {
              "samples": [
                "57:3",
                0
              ],
              "vae": [
                "57:29",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "57:28": {
            "inputs": {
              "unet_name": "z_image_turbo_bf16.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "57:27": {
            "inputs": {
              "text": "{{PROMPT}}",
              "clip": [
                "57:30",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Prompt)"
            }
          },
          "57:13": {
            "inputs": {
              "width": 1024,
              "height": 1024,
              "batch_size": 1
            },
            "class_type": "EmptySD3LatentImage",
            "_meta": {
              "title": "EmptySD3LatentImage"
            }
          },
          "57:11": {
            "inputs": {
              "shift": 3,
              "model": [
                "57:28",
                0
              ]
            },
            "class_type": "ModelSamplingAuraFlow",
            "_meta": {
              "title": "ModelSamplingAuraFlow"
            }
          },
          "57:3": {
            "inputs": {
              "seed": {{SEED}},
              "steps": 8,
              "cfg": 1,
              "sampler_name": "res_multistep",
              "scheduler": "simple",
              "denoise": 1,
              "model": [
                "57:11",
                0
              ],
              "positive": [
                "57:27",
                0
              ],
              "negative": [
                "57:33",
                0
              ],
              "latent_image": [
                "57:13",
                0
              ]
            },
            "class_type": "KSampler",
            "_meta": {
              "title": "KSampler"
            }
          }
        }
        """;

    private const string Flux2 = """
        {
          "9": {
            "inputs": {
              "filename_prefix": "Flux2",
              "images": [
                "68:8",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "45": {
            "inputs": {
              "upscale_method": "lanczos",
              "megapixels": 1,
              "resolution_steps": 1,
              "image": [
                "46",
                0
              ]
            },
            "class_type": "ImageScaleToTotalPixels",
            "_meta": {
              "title": "Scale Image to Total Pixels"
            }
          },
          "46": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "68:48": {
            "inputs": {
              "steps": [
                "68:93",
                0
              ],
              "width": [
                "68:72",
                0
              ],
              "height": [
                "68:72",
                1
              ]
            },
            "class_type": "Flux2Scheduler",
            "_meta": {
              "title": "Flux2Scheduler"
            }
          },
          "68:22": {
            "inputs": {
              "model": [
                "68:92",
                0
              ],
              "conditioning": [
                "68:43",
                0
              ]
            },
            "class_type": "BasicGuider",
            "_meta": {
              "title": "Basic Guider"
            }
          },
          "68:16": {
            "inputs": {
              "sampler_name": "euler"
            },
            "class_type": "KSamplerSelect",
            "_meta": {
              "title": "KSamplerSelect"
            }
          },
          "68:10": {
            "inputs": {
              "vae_name": "full_encoder_small_decoder.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "68:13": {
            "inputs": {
              "noise": [
                "68:25",
                0
              ],
              "guider": [
                "68:22",
                0
              ],
              "sampler": [
                "68:16",
                0
              ],
              "sigmas": [
                "68:48",
                0
              ],
              "latent_image": [
                "68:47",
                0
              ]
            },
            "class_type": "SamplerCustomAdvanced",
            "_meta": {
              "title": "SamplerCustomAdvanced"
            }
          },
          "68:6": {
            "inputs": {
              "text": "{{PROMPT}}",
              "clip": [
                "68:38",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Positive Prompt)"
            }
          },
          "68:38": {
            "inputs": {
              "clip_name": "mistral_3_small_flux2_bf16.safetensors",
              "type": "flux2",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "68:25": {
            "inputs": {
              "noise_seed": {{SEED}}
            },
            "class_type": "RandomNoise",
            "_meta": {
              "title": "RandomNoise"
            }
          },
          "68:8": {
            "inputs": {
              "samples": [
                "68:13",
                0
              ],
              "vae": [
                "68:10",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "68:26": {
            "inputs": {
              "guidance": 4,
              "conditioning": [
                "68:6",
                0
              ]
            },
            "class_type": "FluxGuidance",
            "_meta": {
              "title": "FluxGuidance"
            }
          },
          "68:89": {
            "inputs": {
              "lora_name": "Flux_2-Turbo-LoRA_comfyui.safetensors",
              "strength_model": 1,
              "model": [
                "68:12",
                0
              ]
            },
            "class_type": "LoraLoaderModelOnly",
            "_meta": {
              "title": "Load LoRA"
            }
          },
          "68:12": {
            "inputs": {
              "unet_name": "flux2_dev_fp8mixed.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "68:92": {
            "inputs": {
              "switch": [
                "68:94",
                0
              ],
              "on_false": [
                "68:12",
                0
              ],
              "on_true": [
                "68:89",
                0
              ]
            },
            "class_type": "ComfySwitchNode",
            "_meta": {
              "title": "Switch(model)"
            }
          },
          "68:90": {
            "inputs": {
              "value": 8
            },
            "class_type": "PrimitiveInt",
            "_meta": {
              "title": "Steps"
            }
          },
          "68:91": {
            "inputs": {
              "value": 20
            },
            "class_type": "PrimitiveInt",
            "_meta": {
              "title": "Steps"
            }
          },
          "68:93": {
            "inputs": {
              "switch": [
                "68:94",
                0
              ],
              "on_false": [
                "68:91",
                0
              ],
              "on_true": [
                "68:90",
                0
              ]
            },
            "class_type": "ComfySwitchNode",
            "_meta": {
              "title": "Switch(steps)"
            }
          },
          "68:47": {
            "inputs": {
              "width": [
                "68:72",
                0
              ],
              "height": [
                "68:72",
                1
              ],
              "batch_size": 1
            },
            "class_type": "EmptyFlux2LatentImage",
            "_meta": {
              "title": "Empty Flux 2 Latent"
            }
          },
          "68:72": {
            "inputs": {
              "image": [
                "45",
                0
              ]
            },
            "class_type": "GetImageSize",
            "_meta": {
              "title": "Get Image Size"
            }
          },
          "68:44": {
            "inputs": {
              "pixels": [
                "45",
                0
              ],
              "vae": [
                "68:10",
                0
              ]
            },
            "class_type": "VAEEncode",
            "_meta": {
              "title": "VAE Encode"
            }
          },
          "68:43": {
            "inputs": {
              "conditioning": [
                "68:26",
                0
              ],
              "latent": [
                "68:44",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          },
          "68:94": {
            "inputs": {
              "value": false
            },
            "class_type": "PrimitiveBoolean",
            "_meta": {
              "title": "Enable 8 steps lora"
            }
          }
        }
        """;

    private const string Flux2KleinEditDouble = """
        {
          "76": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "81": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME_2}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "94": {
            "inputs": {
              "filename_prefix": "Flux2-Klein-4b-base",
              "images": [
                "92:104",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "92:102": {
            "inputs": {
              "sampler_name": "euler"
            },
            "class_type": "KSamplerSelect",
            "_meta": {
              "title": "KSamplerSelect"
            }
          },
          "92:103": {
            "inputs": {
              "noise": [
                "92:105",
                0
              ],
              "guider": [
                "92:114",
                0
              ],
              "sampler": [
                "92:102",
                0
              ],
              "sigmas": [
                "92:115",
                0
              ],
              "latent_image": [
                "92:109",
                0
              ]
            },
            "class_type": "SamplerCustomAdvanced",
            "_meta": {
              "title": "SamplerCustomAdvanced"
            }
          },
          "92:104": {
            "inputs": {
              "samples": [
                "92:103",
                0
              ],
              "vae": [
                "92:107",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "92:105": {
            "inputs": {
              "noise_seed": {{SEED}}
            },
            "class_type": "RandomNoise",
            "_meta": {
              "title": "RandomNoise"
            }
          },
          "92:106": {
            "inputs": {
              "unet_name": "flux-2-klein-base-9b-fp8.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "92:107": {
            "inputs": {
              "vae_name": "full_encoder_small_decoder.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "92:108": {
            "inputs": {
              "image": [
                "92:110",
                0
              ]
            },
            "class_type": "GetImageSize",
            "_meta": {
              "title": "Get Image Size"
            }
          },
          "92:109": {
            "inputs": {
              "width": [
                "92:108",
                0
              ],
              "height": [
                "92:108",
                1
              ],
              "batch_size": 1
            },
            "class_type": "EmptyFlux2LatentImage",
            "_meta": {
              "title": "Empty Flux 2 Latent"
            }
          },
          "92:110": {
            "inputs": {
              "upscale_method": "lanczos",
              "megapixels": 1,
              "resolution_steps": 1,
              "image": [
                "76",
                0
              ]
            },
            "class_type": "ImageScaleToTotalPixels",
            "_meta": {
              "title": "Scale Image to Total Pixels"
            }
          },
          "92:85": {
            "inputs": {
              "upscale_method": "lanczos",
              "megapixels": 1,
              "resolution_steps": 1,
              "image": [
                "81",
                0
              ]
            },
            "class_type": "ImageScaleToTotalPixels",
            "_meta": {
              "title": "Scale Image to Total Pixels"
            }
          },
          "92:111": {
            "inputs": {
              "clip_name": "qwen_3_8b_fp8mixed.safetensors",
              "type": "flux2",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "92:113": {
            "inputs": {
              "text": "{{PROMPT}}",
              "clip": [
                "92:111",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Positive Prompt)"
            }
          },
          "92:87": {
            "inputs": {
              "text": "",
              "clip": [
                "92:111",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode ( Negative Prompt)"
            }
          },
          "92:114": {
            "inputs": {
              "cfg": 5,
              "model": [
                "92:106",
                0
              ],
              "positive": [
                "92:130",
                0
              ],
              "negative": [
                "92:128",
                0
              ]
            },
            "class_type": "CFGGuider",
            "_meta": {
              "title": "CFG Guider"
            }
          },
          "92:115": {
            "inputs": {
              "steps": 20,
              "width": [
                "92:108",
                0
              ],
              "height": [
                "92:108",
                1
              ]
            },
            "class_type": "Flux2Scheduler",
            "_meta": {
              "title": "Flux2Scheduler"
            }
          },
          "92:125": {
            "inputs": {
              "conditioning": [
                "92:87",
                0
              ],
              "latent": [
                "92:126",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          },
          "92:126": {
            "inputs": {
              "pixels": [
                "92:110",
                0
              ],
              "vae": [
                "92:107",
                0
              ]
            },
            "class_type": "VAEEncode",
            "_meta": {
              "title": "VAE Encode"
            }
          },
          "92:127": {
            "inputs": {
              "conditioning": [
                "92:113",
                0
              ],
              "latent": [
                "92:126",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          },
          "92:128": {
            "inputs": {
              "conditioning": [
                "92:125",
                0
              ],
              "latent": [
                "92:129",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          },
          "92:129": {
            "inputs": {
              "pixels": [
                "92:85",
                0
              ],
              "vae": [
                "92:107",
                0
              ]
            },
            "class_type": "VAEEncode",
            "_meta": {
              "title": "VAE Encode"
            }
          },
          "92:130": {
            "inputs": {
              "conditioning": [
                "92:127",
                0
              ],
              "latent": [
                "92:129",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          }
        }
        """;

    private const string Flux2KleinInpaintReference = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}}" } },
          "2": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME_2}}" } },
          "3": { "class_type": "UNETLoader", "inputs": { "unet_name": "flux-2-klein-base-9b-fp8.safetensors", "weight_dtype": "default" } },
          "4": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_3_8b_fp8mixed.safetensors", "type": "flux2", "device": "default" } },
          "5": { "class_type": "VAELoader", "inputs": { "vae_name": "full_encoder_small_decoder.safetensors" } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{PROMPT}}", "clip": ["4", 0] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "", "clip": ["4", 0] } },
          "8": { "class_type": "VAEEncode", "inputs": { "pixels": ["1", 0], "vae": ["5", 0] } },
          "9": { "class_type": "SetLatentNoiseMask", "inputs": { "samples": ["8", 0], "mask": ["1", 1] } },
          "10": { "class_type": "VAEEncode", "inputs": { "pixels": ["2", 0], "vae": ["5", 0] } },
          "11": { "class_type": "ReferenceLatent", "inputs": { "conditioning": ["6", 0], "latent": ["8", 0] } },
          "12": { "class_type": "ReferenceLatent", "inputs": { "conditioning": ["11", 0], "latent": ["10", 0] } },
          "13": { "class_type": "ReferenceLatent", "inputs": { "conditioning": ["7", 0], "latent": ["8", 0] } },
          "14": { "class_type": "ReferenceLatent", "inputs": { "conditioning": ["13", 0], "latent": ["10", 0] } },
          "15": { "class_type": "CFGGuider", "inputs": { "cfg": 5, "model": ["3", 0], "positive": ["12", 0], "negative": ["14", 0] } },
          "16": { "class_type": "GetImageSize", "inputs": { "image": ["1", 0] } },
          "17": { "class_type": "Flux2Scheduler", "inputs": { "steps": 20, "width": ["16", 0], "height": ["16", 1] } },
          "18": { "class_type": "KSamplerSelect", "inputs": { "sampler_name": "euler" } },
          "19": { "class_type": "RandomNoise", "inputs": { "noise_seed": {{SEED}} } },
          "20": { "class_type": "SamplerCustomAdvanced", "inputs": { "noise": ["19", 0], "guider": ["15", 0], "sampler": ["18", 0], "sigmas": ["17", 0], "latent_image": ["9", 0] } },
          "21": { "class_type": "VAEDecode", "inputs": { "samples": ["20", 0], "vae": ["5", 0] } },
          "22": { "class_type": "ImageCompositeMasked", "inputs": { "destination": ["1", 0], "source": ["21", 0], "x": 0, "y": 0, "resize_source": false, "mask": ["1", 1] } },
          "23": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Flux2_Klein_Inpaint_Reference", "images": ["22", 0] } }
        }
        """;

    private const string Flux2KleinEditSingle = """
        {
          "9": {
            "inputs": {
              "filename_prefix": "Flux2-Klein-4b-base",
              "images": [
                "75:65",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "76": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "75:61": {
            "inputs": {
              "sampler_name": "euler"
            },
            "class_type": "KSamplerSelect",
            "_meta": {
              "title": "KSamplerSelect"
            }
          },
          "75:62": {
            "inputs": {
              "steps": 20,
              "width": [
                "75:100",
                0
              ],
              "height": [
                "75:100",
                1
              ]
            },
            "class_type": "Flux2Scheduler",
            "_meta": {
              "title": "Flux2Scheduler"
            }
          },
          "75:63": {
            "inputs": {
              "cfg": 5,
              "model": [
                "75:70",
                0
              ],
              "positive": [
                "75:124",
                0
              ],
              "negative": [
                "75:122",
                0
              ]
            },
            "class_type": "CFGGuider",
            "_meta": {
              "title": "CFG Guider"
            }
          },
          "75:64": {
            "inputs": {
              "noise": [
                "75:73",
                0
              ],
              "guider": [
                "75:63",
                0
              ],
              "sampler": [
                "75:61",
                0
              ],
              "sigmas": [
                "75:62",
                0
              ],
              "latent_image": [
                "75:66",
                0
              ]
            },
            "class_type": "SamplerCustomAdvanced",
            "_meta": {
              "title": "SamplerCustomAdvanced"
            }
          },
          "75:65": {
            "inputs": {
              "samples": [
                "75:64",
                0
              ],
              "vae": [
                "75:72",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "75:73": {
            "inputs": {
              "noise_seed": {{SEED}}
            },
            "class_type": "RandomNoise",
            "_meta": {
              "title": "RandomNoise"
            }
          },
          "75:70": {
            "inputs": {
              "unet_name": "flux-2-klein-base-9b-fp8.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "75:71": {
            "inputs": {
              "clip_name": "qwen_3_8b_fp8mixed.safetensors",
              "type": "flux2",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "75:74": {
            "inputs": {
              "text": "{{PROMPT}}",
              "clip": [
                "75:71",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Positive Prompt)"
            }
          },
          "75:67": {
            "inputs": {
              "text": "",
              "clip": [
                "75:71",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Negative Prompt)"
            }
          },
          "75:72": {
            "inputs": {
              "vae_name": "full_encoder_small_decoder.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "75:66": {
            "inputs": {
              "width": [
                "75:100",
                0
              ],
              "height": [
                "75:100",
                1
              ],
              "batch_size": 1
            },
            "class_type": "EmptyFlux2LatentImage",
            "_meta": {
              "title": "Empty Flux 2 Latent"
            }
          },
          "75:80": {
            "inputs": {
              "upscale_method": "lanczos",
              "megapixels": 1,
              "resolution_steps": 1,
              "image": [
                "76",
                0
              ]
            },
            "class_type": "ImageScaleToTotalPixels",
            "_meta": {
              "title": "Scale Image to Total Pixels"
            }
          },
          "75:100": {
            "inputs": {
              "image": [
                "75:80",
                0
              ]
            },
            "class_type": "GetImageSize",
            "_meta": {
              "title": "Get Image Size"
            }
          },
          "75:122": {
            "inputs": {
              "conditioning": [
                "75:67",
                0
              ],
              "latent": [
                "75:123",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          },
          "75:123": {
            "inputs": {
              "pixels": [
                "75:80",
                0
              ],
              "vae": [
                "75:72",
                0
              ]
            },
            "class_type": "VAEEncode",
            "_meta": {
              "title": "VAE Encode"
            }
          },
          "75:124": {
            "inputs": {
              "conditioning": [
                "75:74",
                0
              ],
              "latent": [
                "75:123",
                0
              ]
            },
            "class_type": "ReferenceLatent",
            "_meta": {
              "title": "Set Reference Latent"
            }
          }
        }
        """;

    private const string Krea2StyleReference = """
        {
          "29": {
            "inputs": {
              "filename_prefix": "Krea2_turbo_style_reference",
              "images": [
                "30:62",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "69": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "71": {
            "inputs": {
              "aspect_ratio": "1:1 (Square)",
              "megapixels": 1,
              "multiple": 8
            },
            "class_type": "ResolutionSelector",
            "_meta": {
              "title": "Resolution Selector"
            }
          },
          "30:5": {
            "inputs": {
              "width": [
                "71",
                0
              ],
              "height": [
                "71",
                1
              ],
              "batch_size": 1
            },
            "class_type": "EmptyLatentImage",
            "_meta": {
              "title": "Empty Latent Image"
            }
          },
          "30:10": {
            "inputs": {
              "unet_name": "krea2_turbo_int8_convrot.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "30:11": {
            "inputs": {
              "clip_name": "qwen3vl_4b_fp8_scaled.safetensors",
              "type": "krea2",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "30:12": {
            "inputs": {
              "vae_name": "qwen_image_vae.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "30:13": {
            "inputs": {
              "conditioning": [
                "30:53",
                0
              ]
            },
            "class_type": "ConditioningZeroOut",
            "_meta": {
              "title": "Conditioning Zero Out"
            }
          },
          "30:15": {
            "inputs": {
              "lora_name": "krea2_style_reference.safetensors",
              "strength_model": 1,
              "model": [
                "30:10",
                0
              ]
            },
            "class_type": "LoraLoaderModelOnly",
            "_meta": {
              "title": "Load LoRA"
            }
          },
          "30:16": {
            "inputs": {
              "prompt": [
                "30:17",
                0
              ],
              "max_length": 512,
              "sampling_mode": "on",
              "sampling_mode.temperature": 0.7,
              "sampling_mode.top_k": 64,
              "sampling_mode.top_p": 0.95,
              "sampling_mode.min_p": 0.05,
              "sampling_mode.repetition_penalty": 1.05,
              "sampling_mode.seed": 0,
              "sampling_mode.presence_penalty": 0,
              "thinking": false,
              "use_default_template": true,
              "clip": [
                "30:11",
                0
              ]
            },
            "class_type": "TextGenerate",
            "_meta": {
              "title": "Generate Text"
            }
          },
          "30:17": {
            "inputs": {
              "string_a": [
                "30:18",
                0
              ],
              "string_b": [
                "30:19",
                0
              ],
              "delimiter": ""
            },
            "class_type": "StringConcatenate",
            "_meta": {
              "title": "Concatenate Text"
            }
          },
          "30:18": {
            "inputs": {
              "value": "You are an expert prompt engineer for text-to-image models. Your task is to expand the user's prompt into a highly effective image-generation prompt.\n\nThink step by step about the request before writing the answer:\n- What is the subject and mood?\n- What visual styles, mediums, and lighting options would fit? Consider two or three alternatives and pick the one that best serves the caption.\n- What composition, framing, and grounded details will help the text-to-image model?\n\nThen output a single expanded prompt paragraph.\n\nFollow these rules strictly:\n1. **Faithfulness First:** Preserve all original subjects, actions, colors, and spatial relationships. Do not add new objects, props, characters, or animals unless the user clearly implies them.\n2. **Practical T2I Structure:** Write a prompt that a text-to-image model can parse cleanly. Group subjects with their own attributes and actions. Use grounded phrasing for poses, interactions, and spatial layout.\n3. **Style Planning Stays Internal:** Use your internal reasoning to choose style, medium, framing, and lighting. Do not emit planning tags or wrappers in the visible answer body.\n4. **Text Rendering:** If the user requests visible text, quotes, labels, or typography, specify the exact text clearly and wrap requested words in quotes.\n5. **Avoid Over-Specification:** Do not invent highly specific clothing, colors, materials, or scene details unless the input supports them.\n6. **Structure:** Write one cohesive paragraph after the thinking block. No bullets, JSON, or markdown.\n7. **Respect Existing Detail:** If the user's prompt is already detailed, lightly polish and finalize rather than heavily expanding — preserve their phrasing and direction.\n8. **Respect the Human Form:** Treat depictions of people with dignity. Assume clothing covers genitals and intimate anatomy.\n9. **Preserve User Medium:** When the user explicitly requests a medium (e.g. \"photo of\", \"photograph of\", \"illustration of\", \"painting of\", \"sketch of\", \"3D render of\"), honor it. Do not pivot to a different medium to avoid difficulty — match the user's stated intent.\n\nUser's Input:\n\n"
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "Text String (System Prompt)"
            }
          },
          "30:19": {
            "inputs": {
              "value": "{{PROMPT}}"
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "Text String (User Prompt)"
            }
          },
          "30:20": {
            "inputs": {
              "source": [
                "30:21",
                0
              ]
            },
            "class_type": "PreviewAny",
            "_meta": {
              "title": "Preview as Text"
            }
          },
          "30:21": {
            "inputs": {
              "switch": [
                "30:24",
                0
              ],
              "on_false": [
                "30:19",
                0
              ],
              "on_true": [
                "30:16",
                0
              ]
            },
            "class_type": "ComfySwitchNode",
            "_meta": {
              "title": "If/Else Switch"
            }
          },
          "30:24": {
            "inputs": {
              "value": false
            },
            "class_type": "PrimitiveBoolean",
            "_meta": {
              "title": "Boolean (Refine Prompt?)"
            }
          },
          "30:52": {
            "inputs": {
              "prompt": [
                "30:20",
                0
              ],
              "clip": [
                "30:11",
                0
              ],
              "vae": [
                "30:12",
                0
              ],
              "image1": [
                "69",
                0
              ]
            },
            "class_type": "TextEncodeQwenImageEditPlus",
            "_meta": {
              "title": "TextEncodeQwenImageEditPlus"
            }
          },
          "30:53": {
            "inputs": {
              "reference_latents_method": "index_timestep_zero",
              "conditioning": [
                "30:52",
                0
              ]
            },
            "class_type": "FluxKontextMultiReferenceLatentMethod",
            "_meta": {
              "title": "Edit Model Reference Method"
            }
          },
          "30:57": {
            "inputs": {
              "cfg": 1,
              "model": [
                "30:64",
                0
              ],
              "positive": [
                "30:53",
                0
              ],
              "negative": [
                "30:13",
                0
              ]
            },
            "class_type": "CFGGuider",
            "_meta": {
              "title": "CFG Guider"
            }
          },
          "30:58": {
            "inputs": {
              "noise": [
                "30:63",
                0
              ],
              "guider": [
                "30:57",
                0
              ],
              "sampler": [
                "30:59",
                0
              ],
              "sigmas": [
                "30:60",
                0
              ],
              "latent_image": [
                "30:61",
                0
              ]
            },
            "class_type": "SamplerCustomAdvanced",
            "_meta": {
              "title": "SamplerCustomAdvanced"
            }
          },
          "30:59": {
            "inputs": {
              "sampler_name": "euler"
            },
            "class_type": "KSamplerSelect",
            "_meta": {
              "title": "KSamplerSelect"
            }
          },
          "30:60": {
            "inputs": {
              "scheduler": "simple",
              "steps": 8,
              "denoise": 1,
              "model": [
                "30:64",
                0
              ]
            },
            "class_type": "BasicScheduler",
            "_meta": {
              "title": "BasicScheduler"
            }
          },
          "30:61": {
            "inputs": {
              "width": [
                "71",
                0
              ],
              "height": [
                "71",
                1
              ],
              "batch_size": 1
            },
            "class_type": "EmptyLatentImage",
            "_meta": {
              "title": "Empty Latent Image"
            }
          },
          "30:62": {
            "inputs": {
              "samples": [
                "30:58",
                0
              ],
              "vae": [
                "30:12",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "30:63": {
            "inputs": {
              "noise_seed": {{SEED}}
            },
            "class_type": "RandomNoise",
            "_meta": {
              "title": "RandomNoise"
            }
          },
          "30:64": {
            "inputs": {
              "max_shift": 1.15,
              "base_shift": 0.5,
              "width": [
                "71",
                0
              ],
              "height": [
                "71",
                1
              ],
              "model": [
                "30:15",
                0
              ]
            },
            "class_type": "ModelSamplingFlux",
            "_meta": {
              "title": "ModelSamplingFlux"
            }
          }
        }
        """;

    private const string NetaYumeLuminaT2I = """
        {
          "9": {
            "inputs": {
              "filename_prefix": "NetaYume_Lumina_3.5",
              "images": [
                "48:36",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "48:31": {
            "inputs": {
              "width": 1024,
              "height": 1024,
              "batch_size": 1
            },
            "class_type": "EmptySD3LatentImage",
            "_meta": {
              "title": "EmptySD3LatentImage"
            }
          },
          "48:32": {
            "inputs": {
              "shift": 4,
              "model": [
                "48:34",
                0
              ]
            },
            "class_type": "ModelSamplingAuraFlow",
            "_meta": {
              "title": "ModelSamplingAuraFlow"
            }
          },
          "48:33": {
            "inputs": {
              "seed": {{SEED}},
              "steps": 30,
              "cfg": 4,
              "sampler_name": "res_multistep",
              "scheduler": "simple",
              "denoise": 1,
              "model": [
                "48:32",
                0
              ],
              "positive": [
                "48:50",
                0
              ],
              "negative": [
                "48:35:43",
                0
              ],
              "latent_image": [
                "48:31",
                0
              ]
            },
            "class_type": "KSampler",
            "_meta": {
              "title": "KSampler"
            }
          },
          "48:34": {
            "inputs": {
              "ckpt_name": "NetaYumev35_pretrained_all_in_one.safetensors"
            },
            "class_type": "CheckpointLoaderSimple",
            "_meta": {
              "title": "Load Checkpoint"
            }
          },
          "48:35:40": {
            "inputs": {
              "string_a": [
                "48:35:41",
                0
              ],
              "string_b": [
                "48:35:42",
                0
              ],
              "delimiter": ""
            },
            "class_type": "StringConcatenate",
            "_meta": {
              "title": "Concatenate Text"
            }
          },
          "48:35:41": {
            "inputs": {
              "value": "You are an assistant designed to generate low-quality images based on textual prompts <Prompt Start> "
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "System prompt"
            }
          },
          "48:35:42": {
            "inputs": {
              "value": "blurry, worst quality, low quality, jpeg artifacts, signature, watermark, username, error, deformed hands, bad anatomy, extra limbs, poorly drawn hands, poorly drawn face, mutation, deformed, extra eyes, extra arms, extra legs, malformed limbs, fused fingers, too many fingers, long neck, cross-eyed, bad proportions, missing arms, missing legs, extra digit, fewer digits, cropped"
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "System prompt"
            }
          },
          "48:35:43": {
            "inputs": {
              "text": [
                "48:35:40",
                0
              ],
              "clip": [
                "48:34",
                1
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Negative Prompt)"
            }
          },
          "48:36": {
            "inputs": {
              "samples": [
                "48:33",
                0
              ],
              "vae": [
                "48:34",
                2
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "48:49": {
            "inputs": {
              "string_a": [
                "48:52",
                0
              ],
              "string_b": [
                "48:51",
                0
              ],
              "delimiter": ""
            },
            "class_type": "StringConcatenate",
            "_meta": {
              "title": "Concatenate Text"
            }
          },
          "48:50": {
            "inputs": {
              "text": [
                "48:49",
                0
              ],
              "clip": [
                "48:34",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Positive Prompt)"
            }
          },
          "48:51": {
            "inputs": {
              "value": "{{PROMPT}}"
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "Prompt"
            }
          },
          "48:52": {
            "inputs": {
              "value": "You are an assistant designed to generate high quality anime images based on textual prompts. <Prompt Start> "
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "System prompt"
            }
          }
        }
        """;

    private const string NewbieImageExp01T2I = """
        {
          "9": {
            "inputs": {
              "filename_prefix": "NewBie-Image-Exp0.1",
              "images": [
                "41:8",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "43": {
            "inputs": {
              "string": [
                "47",
                0
              ],
              "find": "{user_prompt}",
              "replace": [
                "48",
                0
              ]
            },
            "class_type": "StringReplace",
            "_meta": {
              "title": "Replace Text"
            }
          },
          "44": {
            "inputs": {
              "value": "An extremely detailed, vibrant, and dynamic digital illustration in an anime style features a young girl, identified as $character_1$, in an extreme close-up, tight shot, captured with a dramatic Dutch angle[cite: 36, 38]. $Character_1$, who resembles Roxy Migurdia, is positioned to fill the entire frame in the center-foreground, creating an intense, immersive perspective[cite: 15, 32]. She has long, bright **blue hair** that appears to be floating dynamically around her head and shoulders, with a small **ahoge** visible on top[cite: 31, 32]. Her intense, **concerned blue eyes** with sharp pupils stare directly out of the frame toward the viewer, conveying a serious and focused expression, categorized as **jitome**[cite: 31, 34]. The top half of her face is obscured by a shadow cast by her very large, wide-brimmed **black witch hat**, creating a dramatic **cut light** effect that enhances the intense atmosphere and contributes to her shaded face[cite: 36, 37]. She is wearing a complex, **multicolored outfit** that includes a high-collared garment, possibly a coat or shawl, over what appears to be a **white and blue dress** with intricate trim, suggesting a magical or fantasy setting[cite: 31]. The clothing is obscured in parts by the surrounding special effects[cite: 38]. Her action is highly dynamic: her left arm extends dramatically out of the image plane, with her **hand open and pointing towards the camera**[cite: 32]. Her right hand is used to hold or adjust the brim of the large hat on her head[cite: 32]. The entire scene is enveloped in a powerful, dynamic effect of **fluid dynamics** and a **vortex**[cite: 36, 38]. **Cerulean blue water, or fluid**, is spiraling and swirling around her body and hand, creating a **three-dimensional spiral structure** that dominates the composition[cite: 38]. Scattered among the fluid are multiple sharp, glowing **blue crystal petals** and shards, giving the scene a magical and crystalline quality[cite: 37, 38]. The art style is defined by high resolution, best quality, and master-level detail, with noticeable artistic effects such as **chromatic aberration** and **bokeh** enhancing the sense of motion and depth[cite: 37]. The dark, abstract background further emphasizes the character and the vibrant blue light of the surrounding magical effects[cite: 36]. The lighting is highly dramatic, with a strong, artificial light source highlighting the crystalline fluids and the bottom of her face[cite: 37]. This is an illustration with an epic composition[cite: 36]."
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "Caption"
            }
          },
          "46": {
            "inputs": {
              "string": [
                "43",
                0
              ],
              "find": "{caption}",
              "replace": [
                "44",
                0
              ]
            },
            "class_type": "StringReplace",
            "_meta": {
              "title": "Replace Text"
            }
          },
          "47": {
            "inputs": {
              "value": "You are an assistant designed to generate high-quality anime images with the highest degree of image-text alignment based on xml format textual prompts. <Prompt Start>\n{\n\"character_1\": {\n\"bbox\": [\n0,\n0,\n1000,\n1000\n],\n\"name\": \"$character_1$\"\n},\n\"image\": {\n\"tags\": \"\n{user_prompt}\n\",\n\"caption\": \"{caption}\"\n}\n}"
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "Prompt Template"
            }
          },
          "48": {
            "inputs": {
              "value": "{{PROMPT}}"
            },
            "class_type": "PrimitiveStringMultiline",
            "_meta": {
              "title": "User Prompt"
            }
          },
          "41:32": {
            "inputs": {
              "shift": 6,
              "model": [
                "41:30",
                0
              ]
            },
            "class_type": "ModelSamplingAuraFlow",
            "_meta": {
              "title": "ModelSamplingAuraFlow"
            }
          },
          "41:30": {
            "inputs": {
              "unet_name": "NewBie-Image-Exp0.1-bf16.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "41:26": {
            "inputs": {
              "vae_name": "ae.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "41:34": {
            "inputs": {
              "clip_name1": "gemma_3_4b_it_bf16.safetensors",
              "clip_name2": "jina_clip_v2_bf16.safetensors",
              "type": "newbie",
              "device": "default"
            },
            "class_type": "DualCLIPLoader",
            "_meta": {
              "title": "Load CLIP (Dual)"
            }
          },
          "41:8": {
            "inputs": {
              "samples": [
                "41:3",
                0
              ],
              "vae": [
                "41:26",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "41:31": {
            "inputs": {
              "width": 1024,
              "height": 1536,
              "batch_size": 1
            },
            "class_type": "EmptySD3LatentImage",
            "_meta": {
              "title": "EmptySD3LatentImage"
            }
          },
          "41:3": {
            "inputs": {
              "seed": {{SEED}},
              "steps": 20,
              "cfg": 5.5,
              "sampler_name": "res_multistep",
              "scheduler": "simple",
              "denoise": 1,
              "model": [
                "41:32",
                0
              ],
              "positive": [
                "41:53",
                0
              ],
              "negative": [
                "41:54",
                0
              ],
              "latent_image": [
                "41:31",
                0
              ]
            },
            "class_type": "KSampler",
            "_meta": {
              "title": "KSampler"
            }
          },
          "41:53": {
            "inputs": {
              "text": [
                "46",
                0
              ],
              "clip": [
                "41:34",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Positive Prompt)"
            }
          },
          "41:54": {
            "inputs": {
              "text": "You are an assistant designed to generate low-quality images based on textual prompts. <Prompt Start>",
              "clip": [
                "41:34",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Negative Prompt)"
            }
          }
        }
        """;

    private const string QwenImageEdit2511 = """
        {
          "9": {
            "inputs": {
              "filename_prefix": "Qwen_Edit_2511",
              "images": [
                "170:158",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "41": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "83": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME_2}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "170:145": {
            "inputs": {
              "shift": 3.1,
              "model": [
                "170:161",
                0
              ]
            },
            "class_type": "ModelSamplingAuraFlow",
            "_meta": {
              "title": "ModelSamplingAuraFlow"
            }
          },
          "170:146": {
            "inputs": {
              "vae_name": "qwen_image_vae.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "170:147": {
            "inputs": {
              "reference_latents_method": "index_timestep_zero",
              "conditioning": [
                "170:149",
                0
              ]
            },
            "class_type": "FluxKontextMultiReferenceLatentMethod",
            "_meta": {
              "title": "Edit Model Reference Method"
            }
          },
          "170:148": {
            "inputs": {
              "reference_latents_method": "index_timestep_zero",
              "conditioning": [
                "170:151",
                0
              ]
            },
            "class_type": "FluxKontextMultiReferenceLatentMethod",
            "_meta": {
              "title": "Edit Model Reference Method"
            }
          },
          "170:149": {
            "inputs": {
              "prompt": "",
              "clip": [
                "170:162",
                0
              ],
              "vae": [
                "170:146",
                0
              ],
              "image1": [
                "170:160",
                0
              ],
              "image2": [
                "83",
                0
              ]
            },
            "class_type": "TextEncodeQwenImageEditPlus",
            "_meta": {
              "title": "TextEncodeQwenImageEditPlus"
            }
          },
          "170:151": {
            "inputs": {
              "prompt": "{{PROMPT}}",
              "clip": [
                "170:162",
                0
              ],
              "vae": [
                "170:146",
                0
              ],
              "image1": [
                "170:160",
                0
              ],
              "image2": [
                "83",
                0
              ]
            },
            "class_type": "TextEncodeQwenImageEditPlus",
            "_meta": {
              "title": "TextEncodeQwenImageEditPlus (Positive)"
            }
          },
          "170:152": {
            "inputs": {
              "strength": 1,
              "pre_cfg": false,
              "model": [
                "170:145",
                0
              ]
            },
            "class_type": "CFGNorm",
            "_meta": {
              "title": "CFGNorm"
            }
          },
          "170:153": {
            "inputs": {
              "lora_name": "Qwen-Image-Edit-2511-Lightning-4steps-V1.0-bf16.safetensors",
              "strength_model": 1,
              "model": [
                "170:152",
                0
              ]
            },
            "class_type": "LoraLoaderModelOnly",
            "_meta": {
              "title": "Load LoRA"
            }
          },
          "170:154": {
            "inputs": {
              "value": 4
            },
            "class_type": "PrimitiveFloat",
            "_meta": {
              "title": "CFG"
            }
          },
          "170:155": {
            "inputs": {
              "value": 1
            },
            "class_type": "PrimitiveFloat",
            "_meta": {
              "title": "CFG"
            }
          },
          "170:156": {
            "inputs": {
              "pixels": [
                "170:160",
                0
              ],
              "vae": [
                "170:146",
                0
              ]
            },
            "class_type": "VAEEncode",
            "_meta": {
              "title": "VAE Encode"
            }
          },
          "170:161": {
            "inputs": {
              "unet_name": "qwen_image_edit_2511_fp8mixed.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "170:162": {
            "inputs": {
              "clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors",
              "type": "qwen_image",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "170:163": {
            "inputs": {
              "switch": [
                "170:168",
                0
              ],
              "on_false": [
                "170:152",
                0
              ],
              "on_true": [
                "170:153",
                0
              ]
            },
            "class_type": "ComfySwitchNode",
            "_meta": {
              "title": "Switch (Model)"
            }
          },
          "170:165": {
            "inputs": {
              "value": 4
            },
            "class_type": "PrimitiveInt",
            "_meta": {
              "title": "Steps"
            }
          },
          "170:166": {
            "inputs": {
              "value": 40
            },
            "class_type": "PrimitiveInt",
            "_meta": {
              "title": "Steps"
            }
          },
          "170:168": {
            "inputs": {
              "value": false
            },
            "class_type": "PrimitiveBoolean",
            "_meta": {
              "title": "Enable 4steps LoRA?"
            }
          },
          "170:164": {
            "inputs": {
              "switch": [
                "170:168",
                0
              ],
              "on_false": [
                "170:154",
                0
              ],
              "on_true": [
                "170:155",
                0
              ]
            },
            "class_type": "ComfySwitchNode",
            "_meta": {
              "title": "Switch (CFG)"
            }
          },
          "170:167": {
            "inputs": {
              "switch": [
                "170:168",
                0
              ],
              "on_false": [
                "170:166",
                0
              ],
              "on_true": [
                "170:165",
                0
              ]
            },
            "class_type": "ComfySwitchNode",
            "_meta": {
              "title": "Switch (Steps)"
            }
          },
          "170:169": {
            "inputs": {
              "seed": {{SEED}},
              "steps": [
                "170:167",
                0
              ],
              "cfg": [
                "170:164",
                0
              ],
              "sampler_name": "euler",
              "scheduler": "simple",
              "denoise": 1,
              "model": [
                "170:163",
                0
              ],
              "positive": [
                "170:148",
                0
              ],
              "negative": [
                "170:147",
                0
              ],
              "latent_image": [
                "170:156",
                0
              ]
            },
            "class_type": "KSampler",
            "_meta": {
              "title": "KSampler"
            }
          },
          "170:158": {
            "inputs": {
              "samples": [
                "170:169",
                0
              ],
              "vae": [
                "170:146",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "170:160": {
            "inputs": {
              "image": [
                "41",
                0
              ]
            },
            "class_type": "FluxKontextImageScale",
            "_meta": {
              "title": "FluxKontextImageScale"
            }
          }
        }
        """;

    private const string QwenInstantXUnionControlNet = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}}" } },
          "2": { "class_type": "UNETLoader", "inputs": { "unet_name": "qwen_image_fp8_e4m3fn.safetensors", "weight_dtype": "default" } },
          "3": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors", "type": "qwen_image", "device": "default" } },
          "4": { "class_type": "VAELoader", "inputs": { "vae_name": "qwen_image_vae.safetensors" } },
          "5": { "class_type": "ControlNetLoader", "inputs": { "control_net_name": "Qwen-Image-InstantX-ControlNet-Union.safetensors" } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{PROMPT}}", "clip": ["3", 0] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "blurry, distorted", "clip": ["3", 0] } },
          "8": { "class_type": "ControlNetApplyAdvanced", "inputs": { "positive": ["6", 0], "negative": ["7", 0], "control_net": ["5", 0], "image": ["1", 0], "vae": ["4", 0], "strength": 1, "start_percent": 0, "end_percent": 1 } },
          "9": { "class_type": "VAEEncode", "inputs": { "pixels": ["1", 0], "vae": ["4", 0] } },
          "10": { "class_type": "ModelSamplingAuraFlow", "inputs": { "model": ["2", 0], "shift": 3.1 } },
          "11": { "class_type": "KSampler", "inputs": { "seed": {{SEED}}, "steps": 20, "cfg": 4, "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["10", 0], "positive": ["8", 0], "negative": ["8", 1], "latent_image": ["9", 0] } },
          "12": { "class_type": "VAEDecode", "inputs": { "samples": ["11", 0], "vae": ["4", 0] } },
          "13": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Qwen_InstantX_Union", "images": ["12", 0] } }
        }
        """;

    private const string QwenImageEdit2511Inpainting = """
        {
          "2": { "inputs": { "clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors", "type": "qwen_image", "device": "default" }, "class_type": "CLIPLoader" },
          "3": { "inputs": { "vae_name": "qwen_image_vae.safetensors" }, "class_type": "VAELoader" },
          "4": { "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}}" }, "class_type": "LoadImage" },
          "6": { "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME_2}}" }, "class_type": "LoadImage" },
          "7": { "inputs": { "pixels": ["4", 0], "vae": ["3", 0] }, "class_type": "VAEEncode" },
          "8": { "inputs": { "samples": ["7", 0], "mask": ["4", 1] }, "class_type": "SetLatentNoiseMask" },
          "9": { "inputs": { "prompt": "{{PROMPT}}", "clip": ["2", 0], "vae": ["3", 0], "image1": ["4", 0], "image2": ["6", 0] }, "class_type": "TextEncodeQwenImageEditPlus" },
          "10": { "inputs": { "text": "ugly, blurry, low quality, deformed, artifacts, watermark, disjointed, bad anatomy", "clip": ["2", 0] }, "class_type": "CLIPTextEncode" },
          "11": { "inputs": { "seed": {{SEED}}, "steps": 40, "cfg": 4.0, "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["15", 0], "positive": ["9", 0], "negative": ["10", 0], "latent_image": ["8", 0] }, "class_type": "KSampler" },
          "12": { "inputs": { "samples": ["11", 0], "vae": ["3", 0] }, "class_type": "VAEDecode" },
          "13": { "inputs": { "filename_prefix": "QwenEdit_Inpaint_Ref", "images": ["16", 0] }, "class_type": "SaveImage" },
          "14": { "inputs": { "unet_name": "qwen_image_edit_2511_bf16.safetensors", "weight_dtype": "default" }, "class_type": "UNETLoader" },
          "15": { "inputs": { "model": ["14", 0], "shift": 3.1 }, "class_type": "ModelSamplingAuraFlow" },
          "16": { "inputs": { "destination": ["4", 0], "source": ["12", 0], "x": 0, "y": 0, "resize_source": false, "mask": ["4", 1] }, "class_type": "ImageCompositeMasked" }
        }
        """;

    private const string DWPoseExtractPoseGuide = """
        {
          "1": {
            "class_type": "LoadImage",
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}} [input]"
            }
          },
          "2": {
            "class_type": "DWPreprocessor",
            "inputs": {
              "image": ["1", 0],
              "detect_body": "enable",
              "detect_hand": "enable",
              "detect_face": "enable",
              "resolution": 768,
              "scale_stick_for_xinsr_cn": "disable"
            }
          },
          "3": {
            "class_type": "SaveImage",
            "inputs": {
              "filename_prefix": "DWPose_Pose_Guide",
              "images": ["2", 0]
            }
          }
        }
        """;

    private const string CannyExtractControlGuide = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}} [input]" } },
          "2": { "class_type": "Canny", "inputs": { "image": ["1", 0], "low_threshold": 0.10, "high_threshold": 0.30 } },
          "3": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Canny_Control_Guide", "images": ["2", 0] } }
        }
        """;

    private const string LineartExtractControlGuide = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}} [input]" } },
          "2": { "class_type": "LineArtPreprocessor", "inputs": { "image": ["1", 0], "coarse": "disable" } },
          "3": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Lineart_Control_Guide", "images": ["2", 0] } }
        }
        """;

    private const string DepthExtractControlGuide = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}} [input]" } },
          "2": { "class_type": "DepthAnythingV2Preprocessor", "inputs": { "image": ["1", 0], "ckpt_name": "depth_anything_v2_vitl.pth", "resolution": 768 } },
          "3": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Depth_Control_Guide", "images": ["2", 0] } }
        }
        """;

    private const string NormalExtractControlGuide = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}} [input]" } },
          "2": { "class_type": "BAE-NormalMapPreprocessor", "inputs": { "image": ["1", 0], "resolution": 768 } },
          "3": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Normal_Control_Guide", "images": ["2", 0] } }
        }
        """;

    private const string QwenDWPoseUnionControlNet = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "{{UPLOADED_IMAGE_FILENAME}}" } },
          "2": { "class_type": "DWPreprocessor", "inputs": { "image": ["1", 0], "detect_body": "enable", "detect_hand": "enable", "detect_face": "enable", "resolution": 768, "scale_stick_for_xinsr_cn": "disable" } },
          "3": { "class_type": "UNETLoader", "inputs": { "unet_name": "qwen_image_fp8_e4m3fn.safetensors", "weight_dtype": "default" } },
          "4": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors", "type": "qwen_image", "device": "default" } },
          "5": { "class_type": "VAELoader", "inputs": { "vae_name": "qwen_image_vae.safetensors" } },
          "6": { "class_type": "ControlNetLoader", "inputs": { "control_net_name": "Qwen-Image-InstantX-ControlNet-Union.safetensors" } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{PROMPT}}", "clip": ["4", 0] } },
          "8": { "class_type": "CLIPTextEncode", "inputs": { "text": "blurry, distorted, cropped, bad anatomy", "clip": ["4", 0] } },
          "9": { "class_type": "ControlNetApplyAdvanced", "inputs": { "positive": ["7", 0], "negative": ["8", 0], "control_net": ["6", 0], "image": ["2", 0], "vae": ["5", 0], "strength": 1, "start_percent": 0, "end_percent": 1 } },
          "10": { "class_type": "VAEEncode", "inputs": { "pixels": ["2", 0], "vae": ["5", 0] } },
          "11": { "class_type": "ModelSamplingAuraFlow", "inputs": { "model": ["3", 0], "shift": 3.1 } },
          "12": { "class_type": "KSampler", "inputs": { "seed": {{SEED}}, "steps": 24, "cfg": 4, "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["11", 0], "positive": ["9", 0], "negative": ["9", 1], "latent_image": ["10", 0] } },
          "13": { "class_type": "VAEDecode", "inputs": { "samples": ["12", 0], "vae": ["5", 0] } },
          "14": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Qwen_DWPose_Union", "images": ["13", 0] } }
        }
        """;

    private const string QwenImageInstantXInpainting = """
        {
          "3": {
            "inputs": {
              "seed": {{SEED}},
              "steps": 20,
              "cfg": 2.5,
              "sampler_name": "euler",
              "scheduler": "simple",
              "denoise": 1,
              "model": [
                "66",
                0
              ],
              "positive": [
                "108",
                0
              ],
              "negative": [
                "108",
                1
              ],
              "latent_image": [
                "122",
                0
              ]
            },
            "class_type": "KSampler",
            "_meta": {
              "title": "KSampler"
            }
          },
          "6": {
            "inputs": {
              "text": "{{PROMPT}}",
              "clip": [
                "38",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Positive Prompt)"
            }
          },
          "7": {
            "inputs": {
              "text": " ",
              "clip": [
                "38",
                0
              ]
            },
            "class_type": "CLIPTextEncode",
            "_meta": {
              "title": "CLIP Text Encode (Negative Prompt)"
            }
          },
          "8": {
            "inputs": {
              "samples": [
                "3",
                0
              ],
              "vae": [
                "39",
                0
              ]
            },
            "class_type": "VAEDecode",
            "_meta": {
              "title": "VAE Decode"
            }
          },
          "37": {
            "inputs": {
              "unet_name": "qwen_image_fp8_e4m3fn.safetensors",
              "weight_dtype": "default"
            },
            "class_type": "UNETLoader",
            "_meta": {
              "title": "Load Diffusion Model"
            }
          },
          "38": {
            "inputs": {
              "clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors",
              "type": "qwen_image",
              "device": "default"
            },
            "class_type": "CLIPLoader",
            "_meta": {
              "title": "Load CLIP"
            }
          },
          "39": {
            "inputs": {
              "vae_name": "qwen_image_vae.safetensors"
            },
            "class_type": "VAELoader",
            "_meta": {
              "title": "Load VAE"
            }
          },
          "60": {
            "inputs": {
              "filename_prefix": "ComfyUI",
              "images": [
                "8",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "66": {
            "inputs": {
              "shift": 3.1000000000000005,
              "model": [
                "37",
                0
              ]
            },
            "class_type": "ModelSamplingAuraFlow",
            "_meta": {
              "title": "ModelSamplingAuraFlow"
            }
          },
          "71": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "76": {
            "inputs": {
              "pixels": [
                "172",
                0
              ],
              "vae": [
                "39",
                0
              ]
            },
            "class_type": "VAEEncode",
            "_meta": {
              "title": "VAE Encode"
            }
          },
          "84": {
            "inputs": {
              "control_net_name": "Qwen-Image-InstantX-ControlNet-Inpainting.safetensors"
            },
            "class_type": "ControlNetLoader",
            "_meta": {
              "title": "Load ControlNet Model"
            }
          },
          "108": {
            "inputs": {
              "strength": 1,
              "start_percent": 0,
              "end_percent": 1,
              "positive": [
                "6",
                0
              ],
              "negative": [
                "7",
                0
              ],
              "control_net": [
                "84",
                0
              ],
              "vae": [
                "39",
                0
              ],
              "image": [
                "172",
                0
              ],
              "mask": [
                "121:253",
                0
              ]
            },
            "class_type": "ControlNetInpaintingAliMamaApply",
            "_meta": {
              "title": "Apply ControlNet Inpainting (AliMama)"
            }
          },
          "122": {
            "inputs": {
              "samples": [
                "76",
                0
              ],
              "mask": [
                "121:253",
                0
              ]
            },
            "class_type": "SetLatentNoiseMask",
            "_meta": {
              "title": "Set Latent Noise Mask"
            }
          },
          "124": {
            "inputs": {
              "mask": [
                "121:253",
                0
              ]
            },
            "class_type": "MaskPreview",
            "_meta": {
              "title": "Preview Mask"
            }
          },
          "126": {
            "inputs": {
              "x": 0,
              "y": 0,
              "resize_source": false,
              "destination": [
                "172",
                0
              ],
              "source": [
                "8",
                0
              ],
              "mask": [
                "121:253",
                0
              ]
            },
            "class_type": "ImageCompositeMasked",
            "_meta": {
              "title": "Image Composite Masked"
            }
          },
          "163": {
            "inputs": {
              "filename_prefix": "ComfyUI",
              "images": [
                "126",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "172": {
            "inputs": {
              "upscale_method": "area",
              "largest_size": 1536,
              "image": [
                "71",
                0
              ]
            },
            "class_type": "ImageScaleToMaxDimension",
            "_meta": {
              "title": "Scale Image to Max Dimension"
            }
          },
          "121:253": {
            "inputs": {
              "channel": "red",
              "image": [
                "121:252",
                0
              ]
            },
            "class_type": "ImageToMask",
            "_meta": {
              "title": "Convert Image to Mask"
            }
          },
          "121:251": {
            "inputs": {
              "mask": [
                "121:199",
                0
              ]
            },
            "class_type": "MaskToImage",
            "_meta": {
              "title": "Convert Mask to Image"
            }
          },
          "121:199": {
            "inputs": {
              "expand": 0,
              "tapered_corners": true,
              "mask": [
                "71",
                1
              ]
            },
            "class_type": "GrowMask",
            "_meta": {
              "title": "Grow Mask"
            }
          },
          "121:252": {
            "inputs": {
              "blur_radius": 31,
              "sigma": 1,
              "image": [
                "121:251",
                0
              ]
            },
            "class_type": "ImageBlur",
            "_meta": {
              "title": "Blur Image"
            }
          }
        }
        """;

    private const string ApiBytedanceSeedream4 = """
        {
          "1": {
            "inputs": {
              "model": "seedream-4-0-250828",
              "prompt": "{{PROMPT}}",
              "size_preset": "2048x2048 (1:1)",
              "width": 2048,
              "height": 2048,
              "sequential_image_generation": "disabled",
              "max_images": 1,
              "seed": {{SEED}},
              "watermark": false,
              "fail_on_partial": true,
              "image": [
                "11",
                0
              ]
            },
            "class_type": "ByteDanceSeedreamNode",
            "_meta": {
              "title": "ByteDance Seedream 4.5 & 5.0"
            }
          },
          "11": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "12": {
            "inputs": {
              "filename_prefix": "Seedream-4",
              "images": [
                "1",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          }
        }
        """;

    private const string ApiNanoBananaPro = """
        {
          "11": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "12": {
            "inputs": {
              "image": "{{UPLOADED_IMAGE_FILENAME_2}}"
            },
            "class_type": "LoadImage",
            "_meta": {
              "title": "Load Image"
            }
          },
          "30": {
            "inputs": {
              "filename_prefix": "ComfyUI",
              "images": [
                "35",
                0
              ]
            },
            "class_type": "SaveImage",
            "_meta": {
              "title": "Save Image"
            }
          },
          "35": {
            "inputs": {
              "prompt": "{{PROMPT}}",
              "model": "gemini-3-pro-image-preview",
              "seed": {{SEED}},
              "aspect_ratio": "1:1",
              "resolution": "1K",
              "response_modalities": "IMAGE",
              "system_prompt": "You are an expert image-generation engine. You must ALWAYS produce an image.\nInterpret all user input—regardless of format, intent, or abstraction—as literal visual directives for image composition.\nIf a prompt is conversational or lacks specific visual details, you must creatively invent a concrete visual scenario that depicts the concept.\nPrioritize generating the visual representation above any text, formatting, or conversational requests.",
              "images": [
                "36",
                0
              ]
            },
            "class_type": "GeminiImage2Node",
            "_meta": {
              "title": "Nano Banana Pro (Google Gemini Image)"
            }
          },
          "36": {
            "inputs": {
              "images.image0": [
                "11",
                0
              ],
              "images.image1": [
                "12",
                0
              ]
            },
            "class_type": "BatchImagesNode",
            "_meta": {
              "title": "Batch Images"
            }
          }
        }
        """;
}
