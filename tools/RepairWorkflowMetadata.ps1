$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-ModelName([string] $graph) {
    $known = [regex]::Match($graph, '"(?:unet_name|ckpt_name|model_name|model|model_version)"\s*:\s*"(?<model>[^"]+)"')
    if ($known.Success -and $known.Groups['model'].Value -notin @('default', 'none')) { return $known.Groups['model'].Value }

    $classes = [regex]::Matches($graph, '"class_type"\s*:\s*"(?<class>[^"]+)"') | ForEach-Object { $_.Groups['class'].Value }
    foreach ($class in $classes) {
        $models = @{
            'FluxKontextProImageNode' = 'Flux.1 Kontext Pro'; 'FluxProUltraImageNode' = 'Flux.1 Pro Ultra'
            'FluxProExpandNode' = 'Flux.1 Pro Expand'; 'Flux2MaxImageNode' = 'Flux.2 Max'
            'KlingOmniProImageNode' = 'Kling Omni Pro'; 'KlingImageGenerationNode' = 'Kling V3'
            'MagnificImageRelightNode' = 'Magnific Relight'; 'MagnificImageStyleTransferNode' = 'Magnific Style Transfer'
            'MagnificImageUpscalerPreciseV2Node' = 'Magnific Upscale Precise V2'; 'OpenAIGPTImage1' = 'gpt-image-1'
            'RecraftTextToImageNode' = 'Recraft V3'; 'FluxEraseNode' = 'Flux Erase'
            'TopazImageEnhanceV2' = 'Topaz Wonder 3.5'; 'ResizeImagesByLongerEdge' = 'Magnific Skin Enhancer'
        }
        if ($models.ContainsKey($class)) { return $models[$class] }
    }
    return $null
}

function Apply-WorkflowSemantics($metadata) {
    $inputPurposes = @{
        'image-anima-lllite-any-control-to-image'=@('Control guide'); 'image-anima-lllite-depth-control-to-image'=@('Depth')
        'image-qwen-Image-2512-controlnet'=@('Control guide'); 'image-qwen-image-union-control-lora'=@('Control guide')
        'qwen-diffsynth-canny-controlnet'=@('Canny'); 'qwen-diffsynth-depth-controlnet'=@('Depth')
        'qwen-depth-union-controlnet'=@('Depth'); 'qwen-canny-union-controlnet'=@('Canny')
        'qwen-lineart-union-controlnet'=@('Lineart'); 'qwen-dwpose-union-controlnet'=@('OpenPose')
        'qwen-normal-union-controlnet'=@('Normal'); 'sd3-5-large-blur-controlnet'=@('Blur')
        'sd3-5-large-canny-controlnet'=@('Canny'); 'sd3-5-large-depth-controlnet'=@('Depth')
        'qwen-image-edit-2511-character-pose'=@($null, 'OpenPose')
    }
    if ($inputPurposes.ContainsKey($metadata.id)) {
        $purposes = $inputPurposes[$metadata.id]
        for ($index = 0; $index -lt $purposes.Count -and $index -lt $metadata.capabilities.imageInputs.Count; $index++) {
            if (-not [string]::IsNullOrWhiteSpace($purposes[$index])) { $metadata.capabilities.imageInputs[$index].purpose = $purposes[$index] }
        }
    }
    $outputPurposes = @{
        'image-lotus-depth-v1-1'='Depth'; 'image-marigold-v2-depth-estimation'='Depth'
        'image-marigold-v2-surface-normal-estimation'='Normal'
    }
    if ($outputPurposes.ContainsKey($metadata.id)) { $metadata.capabilities.outputImagePurpose = $outputPurposes[$metadata.id] }
    $modelOverrides = @{
        'image-anima-lllite-any-control-to-image'='Anima Base v1.0'; 'image-anima-lllite-depth-control-to-image'='Anima Base v1.0'
        'image-anima-lllite-image-inpainting'='Anima Base v1.0'; 'image-marigold-v2-depth-estimation'='Marigold V2'
        'image-marigold-v2-surface-normal-estimation'='Marigold V2'
    }
    if ($modelOverrides.ContainsKey($metadata.id)) { $metadata.capabilities.imageGenerationModel = $modelOverrides[$metadata.id] }
}

function Repair-Metadata($metadataPath) {
    $metadata = Get-Content $metadataPath -Raw | ConvertFrom-Json
    $graphPath = Join-Path (Split-Path -Parent $metadataPath) $metadata.graphFile
    if (-not (Test-Path $graphPath)) { return $false }
    $graph = Get-Content $graphPath -Raw
    # Older imports wrote every second-and-later LoadImage placeholder as the invalid
    # {{UPLOADED_IMAGE_FILENAME_}} token. Restore its sequential, typed placeholders.
    if ($graph.Contains('{{UPLOADED_IMAGE_FILENAME_}}')) {
        $number = 1
        $token = '{{UPLOADED_IMAGE_FILENAME_}}'
        $offset = 0
        while (($position = $graph.IndexOf($token, $offset, [StringComparison]::Ordinal)) -ge 0) {
            $number++
            $replacement = "{{UPLOADED_IMAGE_FILENAME_${number}:image}}"
            $graph = $graph.Substring(0, $position) + $replacement + $graph.Substring($position + $token.Length)
            $offset = $position + $replacement.Length
        }
        [IO.File]::WriteAllText($graphPath, $graph, [Text.UTF8Encoding]::new($false))
        $script:changed++
    }
    $model = Get-ModelName $graph
    $before = $metadata | ConvertTo-Json -Depth 16
    $ordinaryImageCount = ([regex]::Matches($graph, '\{\{UPLOADED_IMAGE_FILENAME(?:_[0-9]+)?:image\}\}')).Count
    if ($metadata.capabilities.minImages -ne $ordinaryImageCount -or
        $metadata.capabilities.maxImages -ne $ordinaryImageCount -or
        $metadata.capabilities.imageInputs.Count -ne $ordinaryImageCount) {
        $metadata.capabilities.minImages = $ordinaryImageCount
        $metadata.capabilities.maxImages = $ordinaryImageCount
        $metadata.capabilities.imageInputs = @(foreach ($index in 1..$ordinaryImageCount) {
            [pscustomobject]@{ acceptsMask = $false; purpose = $null }
        })
    }
    if (-not [string]::IsNullOrWhiteSpace($model)) { $metadata.capabilities.imageGenerationModel = $model }
    Apply-WorkflowSemantics $metadata
    $after = $metadata | ConvertTo-Json -Depth 16
    if ($before -eq $after) { return $false }
    [IO.File]::WriteAllText($metadataPath, $after + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    return $true
}

$changed = 0
Get-ChildItem (Join-Path $root 'Workflows') -Filter '*.meta.json' | ForEach-Object { if (Repair-Metadata $_.FullName) { $changed++ } }

$catalogPath = Join-Path $root 'SlopFactory/Resources/Raw/Workflows/catalog.json'
$catalog = @(Get-Content $catalogPath -Raw | ConvertFrom-Json)
$byId = @{}
Get-ChildItem (Join-Path $root 'Workflows') -Filter '*.meta.json' | ForEach-Object {
    $metadata = Get-Content $_.FullName -Raw | ConvertFrom-Json
    if ($metadata.cloudCompatible -eq $false) { return }
    if (-not $byId.ContainsKey($metadata.id)) { $changed++ }
    # Metadata files are the source of truth for the graph contract shown in the UI.
    $byId[$metadata.id] = $metadata
}
$catalog = @($byId.Values | Sort-Object displayName, id)
[IO.File]::WriteAllText($catalogPath, ($catalog | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Updated $changed metadata entries."
