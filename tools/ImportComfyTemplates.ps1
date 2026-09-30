param([int] $Limit = 0)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-PngPrompt([byte[]] $Bytes) {
    $offset = 8
    while ($offset + 12 -le $Bytes.Length) {
        $length = ([int] $Bytes[$offset] * 16777216) +
            ([int] $Bytes[$offset + 1] * 65536) +
            ([int] $Bytes[$offset + 2] * 256) +
            [int] $Bytes[$offset + 3]
        if ($length -lt 0 -or $offset + 12 + $length -gt $Bytes.Length) { return $null }
        $type = [Text.Encoding]::ASCII.GetString($Bytes, $offset + 4, 4)
        if ($type -eq 'tEXt') {
            $text = [Text.Encoding]::UTF8.GetString($Bytes, $offset + 8, $length)
            if ($text.StartsWith("prompt$([char]0)")) { return $text.Substring(7) }
        }
        $offset += 12 + $length
    }
    return $null
}

function Get-ModelName([System.Collections.IDictionary] $Graph) {
    $modelKeys = @('unet_name', 'ckpt_name', 'model_name', 'model', 'model_version')
    foreach ($node in $Graph.Values) {
        if ($node -isnot [System.Collections.IDictionary] -or $node.inputs -isnot [System.Collections.IDictionary]) { continue }
        foreach ($key in $modelKeys) {
            if ($node.inputs.ContainsKey($key) -and $node.inputs[$key] -is [string] -and $node.inputs[$key] -notin @('default', 'none')) {
                return $node.inputs[$key]
            }
        }
    }
    $apiModels = @{
        'FluxKontextProImageNode'='Flux.1 Kontext Pro'; 'FluxProUltraImageNode'='Flux.1 Pro Ultra'
        'FluxProExpandNode'='Flux.1 Pro Expand'; 'Flux2MaxImageNode'='Flux.2 Max'
        'KlingOmniProImageNode'='Kling Omni Pro'; 'KlingImageGenerationNode'='Kling V3'
        'MagnificImageRelightNode'='Magnific Relight'; 'MagnificImageStyleTransferNode'='Magnific Style Transfer'
        'MagnificImageUpscalerPreciseV2Node'='Magnific Upscale Precise V2'; 'OpenAIGPTImage1'='gpt-image-1'
        'RecraftTextToImageNode'='Recraft V3'; 'FluxEraseNode'='Flux Erase'; 'TopazImageEnhanceV2'='Topaz Wonder 3.5'
    }
    foreach ($node in $Graph.Values) {
        if ($node -is [System.Collections.IDictionary] -and $apiModels.ContainsKey($node.class_type)) { return $apiModels[$node.class_type] }
    }
    return 'Comfy Cloud hosted model'
}

$tree = Invoke-RestMethod 'https://api.github.com/repos/Comfy-Org/workflow_templates/git/trees/dd9769b10b2df75769bd32e349dc0d7927f2a705?recursive=1'
$sources = @{}
$tree.tree | Where-Object { $_.path -like 'templates/*.json' } | ForEach-Object { $sources[$_.path] = $true }
$existing = @{}
Get-ChildItem Workflows -Filter '*.meta.json' | ForEach-Object { $existing[(Get-Content $_.FullName -Raw | ConvertFrom-Json).id] = $true }
$paths = $tree.tree | Where-Object { $_.path -like 'output/*.png' } | ForEach-Object path | Where-Object {
    $_ -match '^output/(api_|image_|flux|sd3|sdxl|z_image)' -and $_ -notmatch '(video|audio|speech|voice|3d|model|svg|text_to_video|image_to_video)'
} | Where-Object { $stem = [IO.Path]::GetFileNameWithoutExtension($_); $sources.ContainsKey("templates/$stem.json") -and -not $existing.ContainsKey(($stem -replace '_', '-')) }
if ($Limit -gt 0) { $paths = $paths | Select-Object -First $Limit }

$temporary = Join-Path $env:TEMP 'slopfactory-comfy-import'
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
foreach ($path in $paths) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($path); $id = $stem -replace '_', '-'
    try {
        $download = Join-Path $temporary ([IO.Path]::GetFileName($path))
        Invoke-WebRequest "https://raw.githubusercontent.com/Comfy-Org/workflow_templates/main/$path" -OutFile $download
        $graph = Get-PngPrompt ([IO.File]::ReadAllBytes($download)) | ConvertFrom-Json -AsHashtable
        $promptNode = $graph.Values | Where-Object {
            $_ -is [System.Collections.IDictionary] -and $_.ContainsKey('inputs') -and
            $_.inputs -is [System.Collections.IDictionary] -and
            $_.inputs.ContainsKey('prompt') -and $_.inputs['prompt'] -is [string]
        } | Select-Object -First 1
        if ($null -ne $promptNode) {
            $promptNode.inputs['prompt'] = '{{PROMPT:string}}'
        } else {
            $promptNode = $graph.Values | Where-Object {
                $_ -is [System.Collections.IDictionary] -and $_.ContainsKey('inputs') -and
                $_.inputs -is [System.Collections.IDictionary] -and
                $_.inputs.ContainsKey('model.prompt') -and $_.inputs['model.prompt'] -is [string]
            } | Select-Object -First 1
            if ($null -ne $promptNode) { $promptNode.inputs['model.prompt'] = '{{PROMPT:string}}' }
        }
        $imageCount = 0
        foreach ($node in $graph.Values) {
            if ($node -isnot [System.Collections.IDictionary] -or -not $node.ContainsKey('inputs') -or
                $node.inputs -isnot [System.Collections.IDictionary] -or $node.class_type -notmatch 'LoadImage') { continue }
            if ($node.inputs.ContainsKey('image') -and $node.inputs['image'] -is [string]) {
                $imageCount++
                $node.inputs['image'] = if ($imageCount -eq 1) { '{{UPLOADED_IMAGE_FILENAME:image}}' } else { "{{UPLOADED_IMAGE_FILENAME_$imageCount:image}}" }
            }
        }
        foreach ($node in $graph.Values) {
            if ($node -isnot [System.Collections.IDictionary] -or -not $node.ContainsKey('inputs') -or
                $node.inputs -isnot [System.Collections.IDictionary]) { continue }
            foreach ($key in @($node.inputs.Keys)) {
                if ($key -match '(^|_)seed$|noise_seed') { $node.inputs[$key] = '{{SEED:seed}}' }
            }
        }
        $json = ($graph | ConvertTo-Json -Depth 100).Replace('"{{SEED:seed}}"', '{{SEED:seed}}')
        [IO.File]::WriteAllText("Workflows/$id.json", $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        $imageInputs = @(); for ($index = 0; $index -lt $imageCount; $index++) { $imageInputs += @{ acceptsMask=$false; purpose=$null } }
        $meta = @{ id=$id; displayName="Comfy Cloud — $($stem -replace '_',' ')"; description="Imported from Comfy Cloud's official $stem template."; graphFile="$id.json"; capabilities=@{ requiresMask=$false; minImages=$imageCount; maxImages=$imageCount; imageInputs=$imageInputs; outputImagePurpose=$null; imageGenerationModel=(Get-ModelName $graph) } }
        [IO.File]::WriteAllText("Workflows/$id.meta.json", ($meta | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Write-Output "Imported $id"
    } catch { Write-Warning "Skipped ${id}: $($_.Exception.Message)" }
}

# Keep the runtime catalog in lockstep with imported graph/metadata files.
& (Join-Path $PSScriptRoot 'RepairWorkflowMetadata.ps1')
