# 安装 SAM 3D Body 运行环境（在 backend 目录下执行）
#   1. 创建 .venv 并安装 CUDA 或 ROCm 版 PyTorch
#   2. clone 上游仓库到 third_party/sam-3d-body
#   3. 安装 posegraph 及 sam3d 可选依赖
#   4. 下载权重并校验 SHA256
#      -Source modelscope（默认）：魔搭镜像，无需登录，国内速度快
#      -Source hf：Hugging Face 官方 gated 仓库，需先申请访问并 `hf auth login`
#      -Backend auto（默认）：有 nvidia-smi 装 CUDA，检测到 AMD HIP 驱动装 Windows ROCm（需 Python 3.12）
param(
    [ValidateSet("vith", "dinov3")][string]$Model = "vith",   # vith（631M）或 dinov3（840M）
    [ValidateSet("modelscope", "hf")][string]$Source = "modelscope",
    [ValidateSet("auto", "cuda", "rocm")][string]$Backend = "auto",
    [string]$TorchIndex = "https://download.pytorch.org/whl/cu128",
    [switch]$SkipWeights
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$venv = Join-Path $root ".venv"
$py = Join-Path $venv "Scripts\python.exe"

function Invoke-Native([string]$File, [string[]]$Arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "命令执行失败（退出码 $LASTEXITCODE）：$File $($Arguments -join ' ')" }
    } finally {
        $ErrorActionPreference = $prev
    }
}

if ($Backend -eq "auto") {
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
        $Backend = "cuda"
    } elseif (Test-Path "$env:SystemRoot\System32\amdhip64_*.dll") {
        $Backend = "rocm"
    } else {
        $Backend = "cuda"
    }
}

# Hugging Face 官方仓库的 LFS SHA256（2025-11-19 发布版），任何来源下载后都用它校验
$officialSha256 = @{
    "vith"   = @{ "model.ckpt" = "3b1cb897f4bbd977bf81cbb0b30780a9582681ac642ee112865790ceb4d66056"
                  "assets/mhr_model.pt" = "352e271a6c42729c68554ceaea0c955e866970160c31e35506d782dc0f7377bc" }
    "dinov3" = @{ "model.ckpt" = "b5a2f9d305dd02626b967aa2e86021fba07065df66ce7a7e00ffb9664f150abf"
                  "assets/mhr_model.pt" = "352e271a6c42729c68554ceaea0c955e866970160c31e35506d782dc0f7377bc" }
}

if (-not (Test-Path $py)) {
    if ($Backend -eq "rocm") {
        # AMD 官方 Windows ROCm PyTorch wheel 需要 Python 3.12（cp312）
        $hasPy312 = $false
        if (Get-Command py -ErrorAction SilentlyContinue) {
            $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
            & py -3.12 -c "import sys" >$null 2>$null
            $hasPy312 = ($LASTEXITCODE -eq 0)
            $ErrorActionPreference = $prev
        }
        if ($hasPy312) {
            Invoke-Native "py" @("-3.12", "-m", "venv", $venv)
        } elseif (Get-Command python3.12 -ErrorAction SilentlyContinue) {
            Invoke-Native "python3.12" @("-m", "venv", $venv)
        } elseif (Get-Command uv -ErrorAction SilentlyContinue) {
            Invoke-Native "uv" @("venv", "--python", "3.12", "--seed", $venv)
        } else {
            Invoke-Native "python" @("-m", "venv", $venv)
        }
    } else {
        Invoke-Native "python" @("-m", "venv", $venv)
    }
    Invoke-Native $py @("-m", "pip", "install", "-U", "pip")
}

$gpuCheck = "import sys`ntry:`n import torch; sys.exit(0 if torch.cuda.is_available() else 1)`nexcept Exception:`n sys.exit(1)"
$prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
& $py -c $gpuCheck
$hasGpuTorch = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prev

if (-not $hasGpuTorch) {
    if ($Backend -eq "rocm" -and -not $PSBoundParameters.ContainsKey("TorchIndex")) {
        $abi = (& $py -c "import sys; print(f'cp{sys.version_info.major}{sys.version_info.minor}')").Trim()
        if ($abi -ne "cp312") {
            throw "AMD 官方 Windows ROCm wheel 需要 Python 3.12（当前 .venv 为 $abi），请安装 Python 3.12 或通过 -TorchIndex 指定源"
        }
        $rocmBase = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1"
        Invoke-Native $py @(
            "-m", "pip", "install", "--no-cache-dir",
            "$rocmBase/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
            "$rocmBase/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
            "$rocmBase/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl",
            "$rocmBase/rocm-7.2.1.tar.gz"
        )
        Invoke-Native $py @(
            "-m", "pip", "install", "--no-cache-dir",
            "$rocmBase/torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl",
            "$rocmBase/torchvision-0.24.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl"
        )
    } else {
        Invoke-Native $py @("-m", "pip", "install", "torch", "torchvision", "--index-url", $TorchIndex)
    }
}

$repo = Join-Path $root "third_party\sam-3d-body"
if (-not (Test-Path $repo)) {
    Invoke-Native "git" @("clone", "--depth", "1", "https://github.com/facebookresearch/sam-3d-body.git", $repo)
}

Invoke-Native $py @("-m", "pip", "install", "-e", "$root[sam3d]")

if ($SkipWeights) { return }

$dest = Join-Path $root "checkpoints\sam-3d-body-$Model"
if ($Source -eq "hf") {
    Invoke-Native (Join-Path $venv "Scripts\hf.exe") @("download", "facebook/sam-3d-body-$Model", "--local-dir", $dest)
} else {
    $base = "https://www.modelscope.cn/models/facebook/sam-3d-body-$Model/resolve/master"
    New-Item -ItemType Directory -Force (Join-Path $dest "assets") | Out-Null
    foreach ($f in "model_config.yaml", "LICENSE", "assets/mhr_model.pt", "model.ckpt") {
        Invoke-Native "curl.exe" @("-L", "-C", "-", "--retry", "3", "-o", (Join-Path $dest $f), "$base/$f")
    }
}

# 权重用 torch.load(weights_only=False) / TorchScript 加载，会执行文件内代码，必须校验来源
foreach ($kv in $officialSha256[$Model].GetEnumerator()) {
    $actual = (Get-FileHash (Join-Path $dest $kv.Key) -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $kv.Value) { throw "SHA256 不匹配：$($kv.Key) 实际 $actual" }
}
Write-Host "权重校验通过：$dest"
