<#
Builds the one-click Posegraph backend package (portable Python + dependencies + SAM 3D Body source + weights).

    powershell -ExecutionPolicy Bypass -File packaging\build_backend_package.ps1 `
        -Python  C:\Users\me\AppData\Local\Programs\Python\Python311 `
        -Venv    D:\dev\backend\.venv `
        -Sam3d   D:\dev\backend\third_party\sam-3d-body `
        -Weights D:\dev\backend\checkpoints\sam-3d-body-vith `
        -Out     D:\dist [-Zip]

-Python  a regular CPython 3.11 install (copied as a portable interpreter; its own site-packages is skipped)
-Venv    the virtualenv the backend runs in (its site-packages is copied: torch CUDA build, fastapi, ...)
-Sam3d   clone of facebookresearch/sam-3d-body (scripts/setup_sam3d.ps1 creates it)
-Weights folder with model.ckpt, model_config.yaml, assets/mhr_model.pt and LICENSE
Result: <Out>\Posegraph-Backend\{python, backend, LICENSES, runtime.json, Start-Backend.bat, README.txt} (+ .zip with -Zip)
#>
param(
    [Parameter(Mandatory)] [string] $Python,
    [Parameter(Mandatory)] [string] $Venv,
    [Parameter(Mandatory)] [string] $Sam3d,
    [Parameter(Mandatory)] [string] $Weights,
    [Parameter(Mandatory)] [string] $Out,
    # Optional: MediaPipe face_landmarker.task for facial expression capture (the venv needs mediapipe installed with --no-deps)
    [string] $FaceModel,
    # Optional: MoGe-2 ViT-B model.pt (depth / camera / floor; the venv needs moge, utils3d_moge, pipeline with --no-deps)
    [string] $MogeModel,
    # Runtime version of this package (Python + dependencies). Bump it together with "runtime" in
    # backend/src/posegraph/version.json whenever dependencies change; the plugin then asks users for the full package.
    [int] $Runtime = 1,
    [switch] $Zip
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$pkg = Join-Path $Out "Posegraph-Backend"

function Copy-Tree($src, $dst, [string[]] $excludeDirs = @(), [string[]] $excludeFiles = @()) {
    $rc = @($src, $dst, "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/R:1", "/W:1")
    if ($excludeDirs) { $rc += "/XD"; $rc += $excludeDirs }
    if ($excludeFiles) { $rc += "/XF"; $rc += $excludeFiles }
    robocopy @rc | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $src -> $dst" }
}

foreach ($p in $Python, $Venv, $Sam3d, $Weights) { if (-not (Test-Path $p)) { throw "Not found: $p" } }
if (-not (Test-Path (Join-Path $Weights "model.ckpt"))) { throw "model.ckpt not found in $Weights" }
if (Test-Path $pkg) { throw "$pkg already exists; remove it first" }
New-Item -ItemType Directory -Force $pkg | Out-Null

Write-Host "1/6 portable Python"
Copy-Tree $Python "$pkg\python" @("$Python\Lib\site-packages", "$Python\Doc", "$Python\Tools", "$Python\tcl", "$Python\Lib\test", "$Python\Lib\idlelib", "$Python\Lib\tkinter", "__pycache__")

Write-Host "2/6 site-packages from the venv (largest part: PyTorch)"
Copy-Tree "$Venv\Lib\site-packages" "$pkg\python\Lib\site-packages" @("posegraph-*", "posesvc-*", "__pycache__") @("__editable__*", "*.pyc")

Write-Host "3/6 backend source"
Copy-Tree "$repo\backend" "$pkg\backend" @("__pycache__", ".venv", "outputs", "third_party", "checkpoints") @("*.pyc")

Write-Host "4/6 SAM 3D Body source"
Copy-Tree $Sam3d "$pkg\backend\third_party\sam-3d-body" @(".git", "__pycache__", "notebook") @("*.pyc")

Write-Host "5/6 weights"
Copy-Tree $Weights "$pkg\backend\checkpoints\sam-3d-body-vith"
if ($FaceModel) {
    New-Item -ItemType Directory -Force "$pkg\backend\checkpoints\face" | Out-Null
    Copy-Item $FaceModel "$pkg\backend\checkpoints\face\face_landmarker.task"
}
if ($MogeModel) {
    New-Item -ItemType Directory -Force "$pkg\backend\checkpoints\moge" | Out-Null
    Copy-Item $MogeModel "$pkg\backend\checkpoints\moge\model.pt"
}

Write-Host "6/6 launcher, readme, licenses"
New-Item -ItemType Directory -Force "$pkg\LICENSES" | Out-Null
Copy-Item "$Weights\LICENSE" "$pkg\LICENSES\SAM-3D-Body-weights-LICENSE.txt"
Copy-Item "$Sam3d\LICENSE" "$pkg\LICENSES\SAM-3D-Body-code-LICENSE.txt"
Copy-Item "$Python\LICENSE.txt" "$pkg\LICENSES\Python-LICENSE.txt"
Copy-Item "$PSScriptRoot\package\*" $pkg -Recurse
$torch = & "$Venv\Scripts\python.exe" -c "import torch; print(torch.__version__)"
$required = (Get-Content "$repo\backend\src\posegraph\version.json" -Raw | ConvertFrom-Json).runtime
if ($required -gt $Runtime) { throw "backend/src/posegraph/version.json requires runtime $required, package is $Runtime" }
@{ runtime = $Runtime; python = (& "$Python\python.exe" -c "import platform; print(platform.python_version())"); torch = "$torch" } |
    ConvertTo-Json | Set-Content "$pkg\runtime.json" -Encoding ascii
if (Test-Path "$repo\LICENSE") { Copy-Item "$repo\LICENSE" "$pkg\LICENSES\Posegraph-LICENSE.txt" }
Copy-Item "$repo\THIRD_PARTY_NOTICES.md" "$pkg\LICENSES\THIRD_PARTY_NOTICES.md"

$size = (Get-ChildItem $pkg -Recurse -File | Measure-Object Length -Sum).Sum / 1GB
Write-Host ("Package folder: {0} ({1:N2} GB)" -f $pkg, $size)

if ($Zip) {
    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
    $archive = Join-Path $Out "Posegraph-Backend-win64.zip"
    if ($sevenZip) {
        & $sevenZip a -tzip -mx=1 -mmt=on $archive $pkg | Out-Null
    } else {
        & "$env:SystemRoot\System32\tar.exe" -a -c -f $archive -C $Out "Posegraph-Backend"
    }
    Write-Host ("Archive: {0} ({1:N2} GB)" -f $archive, ((Get-Item $archive).Length / 1GB))
}

exit 0
