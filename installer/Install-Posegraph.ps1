<#
Posegraph 后端一键安装 / 更新（第一阶段）。双击同目录的 Install-Posegraph.bat 运行。
选安装位置（会找出游戏里正在用的后端、上次装的位置，选了就是更新 / 修复）、显卡类型（自动检测）、网络方式，
取最新的下载清单，准备便携版 Python（3.11；AMD 显卡用 3.12），然后交给 installer.py 装其余的。
装好后游戏没开着就把后端位置填进 Posegraph 的插件设置。上次的选择记在这个文件夹的 installer-settings.json。
不改系统设置：不写注册表、环境变量、PATH、执行策略（.bat 只对这一次运行用 -ExecutionPolicy Bypass）。
#>
param([string]$Root, [ValidateSet("", "direct", "cn", "proxy")][string]$Mode = "", [string]$Proxy = "", [string]$Lang = "",
      [ValidateSet("", "cu128", "cu126", "cpu", "rocm")][string]$Flavor = "", [switch]$NoWait, [switch]$SkipPluginConfig)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$oldEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072   # 老系统也能连 TLS 1.2（只影响这个窗口）
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Lang) { $Lang = if ((Get-UICulture).Name -like "zh*") { "zh" } else { "en" } }
function L([string]$zh, [string]$en) { if ($Lang -eq "zh") { $zh } else { $en } }
function Say([string]$text, [string]$color = "Gray") { Write-Host $text -ForegroundColor $color }
function Ask([string]$prompt) { Write-Host $prompt -ForegroundColor Yellow -NoNewline; return (Read-Host) }
function Quit([int]$code) {
    [Console]::OutputEncoding = $oldEncoding
    Write-Host ""
    if (-not $NoWait) { Read-Host (L "按回车键关闭窗口" "Press Enter to close") | Out-Null }
    exit $code
}

# ---------------------------------------------------------------- 上次的选择
$settingsPath = Join-Path $here "installer-settings.json"
$saved = $null
try { $saved = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
function Save-Settings {
    $data = [ordered]@{ root = $Root; mode = $Mode; proxy = $Proxy; flavor = $Flavor }
    try { [IO.File]::WriteAllText($settingsPath, ($data | ConvertTo-Json), (New-Object Text.UTF8Encoding($false))) } catch { }
}

$lockPath = Join-Path $here "lock.json"
$lock = Get-Content $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.format -ne 2) { Say (L "lock.json 格式不对：请重新下载安装脚本。" "lock.json has an unknown format; download the installer again.") "Red"; Quit 1 }
$version = $lock.posegraph.version

Clear-Host
Say @"

    ____                                          __
   / __ \____  ________  ____ __________ _____  / /_
  / /_/ / __ \/ ___/ _ \/ __ ``/ ___/ __ ``/ __ \/ __ \
 / ____/ /_/ (__  )  __/ /_/ / /  / /_/ / /_/ / / / /
/_/    \____/____/\___/\__, /_/   \__,_/ .___/_/ /_/
                      /____/          /_/
"@ "Cyan"
Say ("  " + (L "照片和插画一键变成 Brio 姿势 · 后端安装 / 更新" "Photos and illustrations to Brio poses · backend install / update")) "White"
Say ("  " + (L "所有文件都在你选的文件夹里，不改系统设置；每个文件都核对 SHA-256。" "Everything stays in the folder you choose, no system settings are changed; every file is checked against SHA-256.")) "DarkGray"
Say ""

# ---------------------------------------------------------------- 已有的后端
function Read-Json([string]$path) { try { return (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null } }
function Test-Package([string]$path) {
    return ($path -and (Test-Path (Join-Path $path "python\python.exe")) -and (Test-Path (Join-Path $path "backend\src\posegraph")))
}
function Get-Flavor([string]$path) {
    $rt = Read-Json (Join-Path $path "runtime.json")
    if ($rt -and $rt.flavor) { return $rt.flavor }
    $torch = Get-ChildItem (Join-Path $path "python\Lib\site-packages") -Filter "torch-*.dist-info" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($torch -and $torch.Name -match '\+(cu\d+|cpu|rocm)') { return $(if ($Matches[1] -like "rocm*") { "rocm" } else { $Matches[1] }) }
    return $null
}
$flavorNames = @{
    "cu128" = (L "NVIDIA · CUDA 12.8" "NVIDIA · CUDA 12.8"); "cu126" = (L "NVIDIA 老显卡 · CUDA 12.6" "older NVIDIA · CUDA 12.6")
    "cpu" = (L "CPU（不用显卡）" "CPU (no GPU)"); "rocm" = (L "AMD · ROCm 7.2.1" "AMD · ROCm 7.2.1")
}
function Describe([string]$path) {
    $v = Read-Json (Join-Path $path "backend\src\posegraph\version.json")
    $f = Get-Flavor $path
    $parts = @()
    if ($v) { $parts += (L "后端 $($v.version)" "backend $($v.version)") }
    if ($f -and $flavorNames[$f]) { $parts += $flavorNames[$f] }
    return ($parts -join " · ")
}

# 游戏里 Posegraph 插件设置里的后端位置（国服 XIVLauncherCN、国际服 XIVLauncher）
$pluginConfigs = @("$env:APPDATA\XIVLauncherCN\pluginConfigs\Posegraph.json", "$env:APPDATA\XIVLauncher\pluginConfigs\Posegraph.json") | Where-Object { Test-Path $_ }
$found = [ordered]@{}
foreach ($cfg in $pluginConfigs) {
    $j = Read-Json $cfg
    if ($j -and $j.BackendPython) {
        $p = Split-Path (Split-Path $j.BackendPython -Parent) -Parent
        if ((Test-Package $p) -and -not $found.Contains($p.ToLower())) { $found[$p.ToLower()] = @{ path = $p; why = (L "游戏里正在用" "in use by the game") } }
    }
}
if ($saved -and $saved.root -and (Test-Package $saved.root) -and -not $found.Contains($saved.root.ToLower())) {
    $found[$saved.root.ToLower()] = @{ path = $saved.root; why = (L "上次选的位置" "last used") }
}

# ---------------------------------------------------------------- 1. 安装 / 更新位置
function Test-Folder([string]$path) {
    if ($path -notmatch '^[A-Za-z]:\\') { return (L "请填完整路径，比如 D:\Posegraph-Backend" "Use a full path such as D:\Posegraph-Backend") }
    if ($path -match '[^\x20-\x7E]') { return (L "路径里不能有中文或其他非英文字符（Python 和一些依赖处理不好）" "The path must contain only English characters") }
    if ($path -match '\\Program Files|\\Windows\\') { return (L "不要装在系统文件夹里" "Don't install into system folders") }
    $existing = (Test-Package $path) -or (Test-Path (Join-Path $path ".install-state.json"))
    if ((Test-Path $path) -and (Get-ChildItem $path -Force | Select-Object -First 1) -and -not $existing) {
        return (L "这个文件夹不是空的，也不是 Posegraph 后端：请换一个空文件夹或新文件夹" "The folder isn't empty and isn't a Posegraph backend; choose an empty or new folder")
    }
    $drive = Get-PSDrive ($path.Substring(0, 1)) -ErrorAction SilentlyContinue
    if (-not $drive) { return (L "找不到这个盘" "Drive not found") }
    if ($drive.Free -lt 16GB -and -not $existing) {
        return (L ("{0} 盘只剩 {1:N1} GB，至少要 16 GB（下载 + 解压）" -f $drive.Name, ($drive.Free / 1GB)) ("Only {1:N1} GB free on {0}:, at least 16 GB needed" -f $drive.Name, ($drive.Free / 1GB)))
    }
    return $null
}

if (-not $Root) {
    Say ("  [1/4] " + (L "装到哪里 / 更新哪一个？" "Install where / update which?")) "Cyan"
    $choices = @($found.Values)
    $newDefault = if ($saved -and $saved.root -and -not (Test-Package $saved.root)) { $saved.root } else {
        $best = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -gt 16GB -and $_.Root -match '^[A-Z]:\\$' } |
            Sort-Object @{ e = { $_.Name -eq "C" } }, @{ e = { -$_.Free } } | Select-Object -First 1
        if ($best) { "$($best.Name):\Posegraph-Backend" } else { "D:\Posegraph-Backend" }
    }
    if ($choices.Count -gt 0) {
        for ($i = 0; $i -lt $choices.Count; $i++) {
            Say ("        $($i + 1)  " + (L "更新 / 修复：" "Update / repair: ") + $choices[$i].path) "White"
            Say ("           " + $choices[$i].why + "  ·  " + (Describe $choices[$i].path)) "DarkGray"
        }
        Say ("        $($choices.Count + 1)  " + (L "装到新的位置" "Install to a new location")) "White"
        Say ("           " + (L "更新只补缺的、换旧的，每个文件都核对，权重和组件不重新下。" "Updating only adds what's missing and replaces what's outdated; weights and components aren't downloaded again.")) "DarkGray"
        while ($true) {
            $answer = (Ask ("        " + (L "选择" "Choice") + " [1]: ")).Trim()
            if (-not $answer) { $answer = "1" }
            if ($answer -match '^\d+$' -and [int]$answer -ge 1 -and [int]$answer -le $choices.Count + 1) { break }
        }
        if ([int]$answer -le $choices.Count) { $Root = $choices[[int]$answer - 1].path }
    }
    if (-not $Root) {
        Say ("        " + (L "需要约 16GB 空间，路径只用英文；选老的一键包文件夹就是更新它。直接回车用默认位置。" "About 16 GB needed, English-only path; choosing an old package folder updates it. Press Enter for the default.")) "DarkGray"
        while ($true) {
            $answer = (Ask ("        " + (L "文件夹" "Folder") + " [$newDefault]: ")).Trim().Trim('"')
            $Root = if ($answer) { $answer } else { $newDefault }
            if ($Root -match '^[A-Za-z]:[\\/]') { $Root = [IO.Path]::GetFullPath($Root) }
            $problem = Test-Folder $Root
            if (-not $problem) { break }
            Say "        × $problem" "Red"
        }
    }
}
if ($Root -match '^[A-Za-z]:[\\/]') { $Root = [IO.Path]::GetFullPath($Root) }
$Root = $Root.TrimEnd([char]92)
$problem = Test-Folder $Root
if ($problem) { Say "  × $Root：$problem" "Red"; Quit 1 }
$updating = Test-Package $Root
$current = if ($updating) { Get-Flavor $Root } else { $null }
if ($updating) { Say ("        " + (L "更新 / 修复这个后端：" "Updating / repairing: ") + "$Root  " + (Describe $Root)) "Green" }
elseif (Test-Path (Join-Path $Root ".install-state.json")) { Say ("        " + (L "检测到上次没装完，会接着装、跳过已经装好的。" "Found an unfinished install; resuming.")) "Green" }
Say ""

# ---------------------------------------------------------------- 2. 显卡
function Get-Gpus {
    $list = @()
    try { $vcs = Get-CimInstance Win32_VideoController -ErrorAction Stop } catch { $vcs = @() }
    foreach ($vc in $vcs) {
        $vendor = if ($vc.PNPDeviceID -match 'VEN_10DE') { "nvidia" } elseif ($vc.PNPDeviceID -match 'VEN_1002') { "amd" } elseif ($vc.PNPDeviceID -match 'VEN_8086') { "intel" } else { "other" }
        $list += [pscustomobject]@{ Name = $vc.Name.Trim(); Vendor = $vendor; Driver = $vc.DriverVersion; Cc = $null; NvDriver = $null; VramMB = $null }
    }
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
        $rows = & nvidia-smi --query-gpu=name,compute_cap,driver_version,memory.total --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -ne 0) { $rows = & nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits 2>$null; $noCc = $true }
        $ErrorActionPreference = $prev
        foreach ($row in @($rows)) {
            $f = $row -split ',\s*'
            if ($noCc) { $f = @($f[0], $null, $f[1], $f[2]) }
            $g = $list | Where-Object { $_.Vendor -eq "nvidia" -and -not $_.NvDriver } | Select-Object -First 1
            if (-not $g) { $g = [pscustomobject]@{ Name = $f[0]; Vendor = "nvidia"; Driver = ""; Cc = $null; NvDriver = $null; VramMB = $null }; $list += $g }
            $g.Name = $f[0].Trim()
            if ($f[1] -match '^\d+(\.\d+)?$') { $g.Cc = [double]$f[1] }
            $g.NvDriver = $f[2].Trim()
            if ($f[3] -match '^\d+$') { $g.VramMB = [int]$f[3] }
        }
    }
    return $list
}

# AMD 官方 Windows ROCm 7.2 支持的显卡（RDNA 4、部分 RDNA 3 和 Ryzen AI Max）
$rocmCards = 'RX\s*90[67]0|RX\s*7900|RX\s*7800|RX\s*7700|PRO\s*W7[789]00|PRO\s*W9700|Radeon\s*8060S|Radeon\s*8050S'
function Get-NvCc($g) {
    if ($g.Cc) { return $g.Cc }
    # 没读到算力（老驱动）：按型号估
    if ($g.Name -match 'RTX\s*50') { return 12.0 } elseif ($g.Name -match 'RTX\s*40') { return 8.9 } elseif ($g.Name -match 'RTX\s*30|RTX\s*A\d') { return 8.6 }
    if ($g.Name -match 'RTX\s*20|GTX\s*16|TITAN\s*RTX') { return 7.5 }
    if ($g.Name -match 'GTX\s*10|TITAN\s*X|GTX\s*9|GTX\s*7') { return 6.1 }
    return 7.5
}

function Get-Recommendation($gpus) {
    $nv = $gpus | Where-Object { $_.Vendor -eq "nvidia" } | Select-Object -First 1
    if ($nv) {
        $cc = Get-NvCc $nv
        $drv = 0.0; [void][double]::TryParse(($nv.NvDriver -replace '[^\d\.]', ''), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$drv)
        if ($nv.VramMB -and $nv.VramMB -lt 3000) { return @("cpu", (L "$($nv.Name) 显存 $($nv.VramMB) MB，不够识别模型用（约 3GB），用 CPU" "$($nv.Name) has $($nv.VramMB) MB VRAM, not enough for the model (about 3 GB); use the CPU")) }
        if ($cc -lt 5.0) { return @("cpu", (L "$($nv.Name) 太老，新版 PyTorch 已经不支持，用 CPU" "$($nv.Name) is too old for current PyTorch; use the CPU")) }
        if ($drv -and $drv -lt 528) { return @("cpu", (L "NVIDIA 驱动 $($nv.NvDriver) 太旧：更新驱动后再运行一次可以用显卡，现在先用 CPU" "NVIDIA driver $($nv.NvDriver) is too old: update it and run again to use the GPU; CPU for now")) }
        if ($cc -lt 7.5) { return @("cu126", (L "$($nv.Name)（算力 $cc）：CUDA 12.8 版 PyTorch 已经不支持 GTX 10 系及更老的卡，用 CUDA 12.6 版" "$($nv.Name) (compute $cc): the CUDA 12.8 PyTorch dropped GTX 10 series and older; use CUDA 12.6")) }
        if ($drv -and $drv -lt 570) { return @("cu126", (L "NVIDIA 驱动 $($nv.NvDriver) 不支持 CUDA 12.8（要 570 以上），用 CUDA 12.6 版；更新驱动后可以换 12.8" "NVIDIA driver $($nv.NvDriver) doesn't support CUDA 12.8 (570+ needed); use CUDA 12.6, or update the driver")) }
        return @("cu128", (L "$($nv.Name)（算力 $cc，驱动 $($nv.NvDriver)）" "$($nv.Name) (compute $cc, driver $($nv.NvDriver))"))
    }
    $amd = $gpus | Where-Object { $_.Vendor -eq "amd" -and $_.Name -match 'RX|PRO|Radeon\s*80[56]0S' } | Select-Object -First 1
    if ($amd) {
        $hip = Test-Path "$env:SystemRoot\System32\amdhip64_7.dll"
        if ($amd.Name -notmatch $rocmCards) { return @("cpu", (L "$($amd.Name) 不在 AMD 官方 Windows ROCm 支持名单里（RX 9000、RX 7900 / 7800 / 7700 等），用 CPU" "$($amd.Name) isn't on AMD's Windows ROCm list (RX 9000, RX 7900 / 7800 / 7700, ...); use the CPU")) }
        if (-not $hip) { return @("cpu", (L "$($amd.Name) 支持 ROCm，但显卡驱动太旧（没有 amdhip64_7.dll）：装最新的 AMD Adrenalin 驱动后选 AMD，现在先用 CPU" "$($amd.Name) supports ROCm but the driver is too old (no amdhip64_7.dll): install the latest AMD Adrenalin driver and choose AMD; CPU for now")) }
        return @("rocm", (L "$($amd.Name)（AMD 官方 ROCm 支持）" "$($amd.Name) (supported by AMD ROCm)"))
    }
    return @("cpu", (L "没找到能用来识别的 NVIDIA / AMD 独立显卡，用 CPU" "No NVIDIA / AMD discrete GPU found for recognition; use the CPU"))
}

$gpus = Get-Gpus
$rec, $why = Get-Recommendation $gpus
if (-not $Flavor) {
    Say ("  [2/4] " + (L "显卡（决定装哪种 PyTorch）" "GPU (decides which PyTorch build)")) "Cyan"
    foreach ($g in $gpus) {
        $extra = @()
        if ($g.Cc) { $extra += (L "算力 $($g.Cc)" "compute $($g.Cc)") }
        if ($g.NvDriver) { $extra += (L "驱动 $($g.NvDriver)" "driver $($g.NvDriver)") }
        if ($g.VramMB) { $extra += "$([math]::Round($g.VramMB / 1024, 1)) GB" }
        Say ("        · $($g.Name)" + $(if ($extra) { "  (" + ($extra -join ", ") + ")" } else { "" })) "DarkGray"
    }
    Say ("        " + (L "建议：" "Suggested: ") + $flavorNames[$rec] + " — " + $why) "Green"
    $options = @(
        @("cu128", (L "NVIDIA 显卡（RTX 20 系及更新，CUDA 12.8，约 2.7 GB）" "NVIDIA (RTX 20 series and newer, CUDA 12.8, about 2.7 GB)")),
        @("cu126", (L "NVIDIA 老显卡（GTX 10 / 16 系等，CUDA 12.6，约 2.5 GB）" "Older NVIDIA (GTX 10 / 16 series, CUDA 12.6, about 2.5 GB)")),
        @("rocm", (L "AMD 显卡（RX 9000、RX 7900 / 7800 / 7700，ROCm 7.2.1 + Python 3.12，约 2.2 GB）" "AMD (RX 9000, RX 7900 / 7800 / 7700, ROCm 7.2.1 + Python 3.12, about 2.2 GB)")),
        @("cpu", (L "不用显卡（CPU，每张约 10~20 秒，下载最少）" "No GPU (CPU, about 10-20 s per image, smallest download)"))
    )
    $default = if ($current -and $current -eq $rec) { $current } else { $rec }
    for ($i = 0; $i -lt $options.Count; $i++) {
        $mark = if ($options[$i][0] -eq $default) { "*" } else { " " }
        $now = if ($current -eq $options[$i][0]) { (L "  ← 现在装的" "  ← installed now") } else { "" }
        Say ("       $mark$($i + 1)  " + $options[$i][1] + $now) "White"
    }
    $defIndex = [array]::IndexOf(@($options | ForEach-Object { $_[0] }), $default) + 1
    while ($true) {
        $answer = (Ask ("        " + (L "选择" "Choice") + " [$defIndex]: ")).Trim()
        if (-not $answer) { $answer = "$defIndex" }
        if ($answer -in "1", "2", "3", "4") { $Flavor = $options[[int]$answer - 1][0]; break }
    }
    if ($Flavor -ne $rec) { Say ("        " + (L "注意：和检测到的建议不一样；装完自检时会告诉你显卡能不能用。" "Note: this differs from the suggestion; the final self-test tells you whether the GPU works.")) "DarkYellow" }
    Say ""
}

# ---------------------------------------------------------------- 3. 网络方式
if (-not $Mode) {
    $last = if ($saved -and $saved.mode) { @{ "direct" = "1"; "cn" = "2"; "proxy" = "3" }[$saved.mode] } else { "" }
    Say ("  [3/4] " + (L "选择网络环境 / 下载方式" "Network / download method")) "Cyan"
    Say ("        1  " + (L "直接下载" "Direct")) "White"
    Say ("           " + (L "从官方地址下（python.org、PyPI、pytorch.org、AMD、GitHub、魔搭）。海外网络，或者已经开了全局代理 / 系统代理时选这个。" "Official sources (python.org, PyPI, pytorch.org, AMD, GitHub, ModelScope). Use this outside China or with a system-wide proxy.")) "DarkGray"
    Say ("        2  " + (L "中国网络（国内镜像 + GitHub 加速）" "China network (mainland mirrors + GitHub accelerators)")) "White"
    Say ("           " + (L "Python 包走清华 / 阿里 / 华为等镜像，PyTorch 走阿里云镜像，GitHub 走公共加速站（多为 Cloudflare 中转），开始前自动测速选最快的。" "Python packages from Tsinghua / Aliyun / Huawei mirrors, PyTorch from Aliyun, GitHub through public accelerators (mostly Cloudflare); the fastest is picked automatically.")) "DarkGray"
    Say ("        3  " + (L "走我自己的代理" "Use my proxy")) "White"
    Say ("           " + (L "填代理地址和端口（比如 Clash 的 127.0.0.1:7897），所有下载都走它。" "Enter proxy address and port (e.g. Clash at 127.0.0.1:7897); all downloads go through it.")) "DarkGray"
    while ($true) {
        $answer = (Ask ("        " + (L "选择" "Choice") + " [$(if ($last) { $last } else { '1/2/3' })]: ")).Trim()
        if (-not $answer -and $last) { $answer = $last }
        if ($answer -in "1", "2", "3") { $Mode = @{ "1" = "direct"; "2" = "cn"; "3" = "proxy" }[$answer]; break }
    }
}
if ($Mode -eq "proxy" -and -not $Proxy) {
    $lastProxy = if ($saved -and $saved.proxy) { $saved.proxy } else { "127.0.0.1:7897" }
    while ($true) {
        $answer = (Ask ("        " + (L "代理地址:端口" "Proxy host:port") + " [$lastProxy]: ")).Trim() -replace '^https?://', '' -replace '/$', ''
        $Proxy = if ($answer) { $answer } else { $lastProxy }
        if ($Proxy -notmatch '^[^:\s]+:\d{1,5}$') { Say ("        × " + (L "格式是 地址:端口，比如 127.0.0.1:7897" "Format: host:port, e.g. 127.0.0.1:7897")) "Red"; continue }
        $hostName, $port = $Proxy.Split(":")
        $tcp = New-Object Net.Sockets.TcpClient
        try {
            $ok = $tcp.ConnectAsync($hostName, [int]$port).Wait(3000)
        } catch { $ok = $false } finally { $tcp.Dispose() }
        if ($ok) { Say ("        √ " + (L "代理连得上" "Proxy reachable")) "Green"; break }
        Say ("        × " + (L "连不上 $Proxy：代理软件开着吗？端口对吗？（Clash 看「端口设置」里的混合端口）" "Can't reach $Proxy. Is the proxy running and the port right?")) "Red"
    }
}
Save-Settings
Say ""

# ---------------------------------------------------------------- 下载用的 HTTP
$handler = New-Object System.Net.Http.HttpClientHandler
if ($Mode -eq "proxy") { $handler.Proxy = New-Object System.Net.WebProxy("http://$Proxy"); $handler.UseProxy = $true }
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromMinutes(5)
$http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 PosegraphInstaller/2.0")

# ---------------------------------------------------------------- 最新的下载清单
# 清单跟着每次发版更新（仓库 main 分支的 installer/lock.json），比这个文件夹里的新就用新的：后端、组件跟着发版走，安装脚本不用重新下。
# 只从 GitHub 本身和 jsDelivr（原样转发 GitHub 的 CDN）取，不走加速站：清单里记着所有文件的 SHA-256，不能经过可能改内容的中转。
Say ("  [4/4] " + (L "取最新的下载清单" "Fetching the latest download list")) "Cyan"
function Compare-Version([string]$a, [string]$b) {
    $x = @($a -split '\D+' | Where-Object { $_ } | ForEach-Object { [int]$_ }); $y = @($b -split '\D+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
    for ($i = 0; $i -lt [math]::Max($x.Count, $y.Count); $i++) {
        $p = if ($i -lt $x.Count) { $x[$i] } else { 0 }; $q = if ($i -lt $y.Count) { $y[$i] } else { 0 }
        if ($p -ne $q) { return [math]::Sign($p - $q) }
    }
    return 0
}
$remote = $null
$quickHandler = New-Object System.Net.Http.HttpClientHandler
if ($Mode -eq "proxy") { $quickHandler.Proxy = New-Object System.Net.WebProxy("http://$Proxy"); $quickHandler.UseProxy = $true }
$quick = New-Object System.Net.Http.HttpClient($quickHandler)
$quick.Timeout = [TimeSpan]::FromSeconds(20)
$quick.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 PosegraphInstaller/2.0")
foreach ($url in @("https://raw.githubusercontent.com/extrant/Posegraph/main/installer/lock.json", "https://cdn.jsdelivr.net/gh/extrant/Posegraph@main/installer/lock.json")) {
    try {
        $text = $quick.GetStringAsync($url).Result
        $remote = $text | ConvertFrom-Json
        if ($remote.posegraph.version) { break }
        $remote = $null
    } catch { $remote = $null }
}
$quick.Dispose()
if ($remote -and $remote.format -gt 2) {
    Say ("        " + (L "有新版的安装脚本（清单格式变了），这次先用自带的清单；想要最新的请重新下载安装脚本。" "A newer installer exists (new list format); using the bundled list this time. Download the installer again for the latest.")) "DarkYellow"
} elseif ($remote -and $remote.format -eq 2 -and ((Compare-Version $remote.posegraph.version $version) -gt 0 -or
        ((Compare-Version $remote.posegraph.version $version) -eq 0 -and [int]$remote.revision -gt [int]$lock.revision))) {
    [IO.File]::WriteAllText($lockPath, $text, (New-Object Text.UTF8Encoding($false)))
    $lock = $remote; $version = $lock.posegraph.version
    Say ("        √ " + (L "用最新的清单：后端 $version" "Using the latest list: backend $version")) "Green"
} elseif ($remote) {
    Say ("        √ " + (L "清单已经是最新的：后端 $version" "The list is up to date: backend $version")) "Green"
} else {
    Say ("        " + (L "取不到最新清单（网络不通），用自带的：后端 $version；装好后在插件里点「更新后端」也能升到最新。" "Couldn't fetch the latest list; using the bundled one (backend $version). 'Update backend' in the plugin can update it later.")) "DarkYellow"
}
Say ""

# ---------------------------------------------------------------- 便携 Python
$envName = $lock.torch.$Flavor.env
$pyInfo = $lock.envs.$envName.python
$pyTag = "python" + (($pyInfo.version -split '\.')[0..1] -join '')    # python311 / python312
Say ("  " + (L "准备便携版 Python $($pyInfo.version)" "Preparing portable Python $($pyInfo.version)")) "Cyan"

function Download([string[]]$urls, [string]$dest, [string]$sha256) {
    foreach ($url in $urls) {
        try {
            $resp = $http.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
            if (-not $resp.IsSuccessStatusCode) { throw "HTTP $([int]$resp.StatusCode)" }
            $total = $resp.Content.Headers.ContentLength
            $in = $resp.Content.ReadAsStreamAsync().Result
            $out = [IO.File]::Create($dest)
            $buf = New-Object byte[] 65536; $got = 0; $sw = [Diagnostics.Stopwatch]::StartNew()
            try {
                while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                    $out.Write($buf, 0, $n); $got += $n
                    if ($sw.ElapsedMilliseconds -gt 150 -or $got -eq $total) {
                        $frac = if ($total) { $got / $total } else { 0 }
                        $w = 30; $full = [int]($frac * $w)
                        Write-Host ("`r        " + ("█" * $full) + ("░" * ($w - $full)) + (" {0,5:P0}  {1:N1}/{2:N1} MB  " -f $frac, ($got / 1MB), ($total / 1MB))) -NoNewline -ForegroundColor Cyan
                        $sw.Restart()
                    }
                }
            } finally { $out.Dispose(); $in.Dispose() }
            Write-Host ""
            $hash = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
            if ($hash -ne $sha256) { throw (L "校验不符" "checksum mismatch") }
            return $true
        } catch {
            $msg = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
            Write-Host ""
            Say ("        × $url  $msg") "DarkYellow"
        }
    }
    return $false
}

$pyDir = Join-Path $Root "python"
$py = Join-Path $pyDir "python.exe"
$oldPy = $null
if ((Test-Path $py) -and -not (Test-Path (Join-Path $pyDir "$pyTag.dll"))) {
    # 换显卡类型要换 Python 版本（AMD 的 ROCm 版 PyTorch 只有 3.12）：整个 Python 环境重装，旧的先挪开，装好再删
    Say ("        " + (L "这次要换成 Python $($pyInfo.version)：整个 Python 环境（依赖、PyTorch）会重新下载安装，权重、源码不动。" "Switching to Python $($pyInfo.version): the whole Python environment (packages, PyTorch) is reinstalled; weights and sources stay.")) "Yellow"
    $answer = (Ask ("        " + (L "继续？[Y/n]: " "Continue? [Y/n]: "))).Trim()
    if ($answer -match '^[nN]') { Quit 1 }
    $oldPy = Join-Path $Root ("python.replaced-" + (Get-Date -Format "yyyyMMddHHmmss"))
    Move-Item $pyDir $oldPy
}
if (-not (Test-Path (Join-Path $pyDir "Lib\os.pyc")) -and -not (Test-Path (Join-Path $pyDir "Lib\os.py"))) {
    New-Item -ItemType Directory -Force $Root | Out-Null
    $zip = Join-Path $Root "python-embed.zip"
    $file = $pyInfo.file; $ver = $pyInfo.version
    $official = "https://www.python.org/ftp/python/$ver/$file"
    $mirrors = @("https://registry.npmmirror.com/-/binary/python/$ver/$file", "https://mirrors.huaweicloud.com/python/$ver/$file")
    $urls = if ($Mode -eq "cn") { $mirrors + $official } else { @($official) + $mirrors }
    if (-not (Download $urls $zip $pyInfo.sha256)) {
        Say ("        " + (L "Python 下载失败，换一种网络方式再试。" "Python download failed; try another network option.")) "Red"
        Quit 1
    }
    # 和普通安装版一样的布局：标准库解压进 Lib（这样不会读到电脑上另外装的 Python），去掉嵌入版的 ._pth（否则 PYTHONPATH 不生效）
    if (Test-Path $pyDir) { Remove-Item $pyDir -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $pyDir)
    $stdlib = Get-ChildItem $pyDir -Filter "python3*.zip" | Select-Object -First 1
    [IO.Compression.ZipFile]::ExtractToDirectory($stdlib.FullName, (Join-Path $pyDir "Lib"))
    Remove-Item $stdlib.FullName, (Join-Path $pyDir "python3*._pth"), $zip -Force
    New-Item -ItemType Directory -Force (Join-Path $pyDir "Lib\site-packages") | Out-Null
    Say ("        √ Python $ver") "Green"
} else {
    Say ("        √ " + (L "已有 Python $($pyInfo.version)，跳过" "Python $($pyInfo.version) already there")) "Green"
}
Say ""

# ---------------------------------------------------------------- 交给 installer.py
$env:PYTHONNOUSERSITE = "1"
$env:PYTHONPATH = ""
$pyArgs = @((Join-Path $here "installer.py"), "--root", $Root, "--mode", $Mode, "--lang", $Lang, "--flavor", $Flavor, "--lock", $lockPath)
if ($Mode -eq "proxy") { $pyArgs += @("--proxy", $Proxy) }
& $py @pyArgs
$code = $LASTEXITCODE
if ($code -ne 0) { Quit $code }
if ($oldPy) { Remove-Item $oldPy -Recurse -Force -ErrorAction SilentlyContinue }
Save-Settings

# ---------------------------------------------------------------- 填进插件设置
# 游戏开着时不改（Dalamud 退出时会把内存里的设置写回去，改了也白改）；改之前备份，只换后端位置这两三项，别的设置原样不动
$wrote = @()
$gameRunning = Get-Process -Name "ffxiv_dx11" -ErrorAction SilentlyContinue
$pluginConfigs = @("$env:APPDATA\XIVLauncherCN\pluginConfigs\Posegraph.json", "$env:APPDATA\XIVLauncher\pluginConfigs\Posegraph.json") | Where-Object { Test-Path $_ }
if (-not $gameRunning -and -not $SkipPluginConfig) {
    foreach ($cfg in $pluginConfigs) {
        try {
            $raw = [IO.File]::ReadAllText($cfg)
            $bom = ([IO.File]::ReadAllBytes($cfg) | Select-Object -First 3) -join "," -eq "239,187,191"
            $pyPath = (Join-Path $Root "python\python.exe") -replace '\\', '\\'
            $dirPath = (Join-Path $Root "backend") -replace '\\', '\\'
            $new = $raw -replace '("BackendPython"\s*:\s*)"[^"]*"', ('$1"' + $pyPath + '"') -replace '("BackendDir"\s*:\s*)"[^"]*"', ('$1"' + $dirPath + '"')
            if ($Flavor -eq "cpu") { $new = $new -replace '("BackendDevice"\s*:\s*)"[^"]*"', '$1"cpu"' }
            elseif ($new -match '"BackendDevice"\s*:\s*"cpu"') { $new = $new -replace '("BackendDevice"\s*:\s*)"[^"]*"', '$1"auto"' }
            if ($new -ne $raw -and $new -match '"BackendPython"') {
                Copy-Item $cfg "$cfg.before-installer.bak" -Force
                [IO.File]::WriteAllText($cfg, $new, (New-Object Text.UTF8Encoding($bom)))
                $wrote += $cfg
            } elseif ($new -eq $raw) { $wrote += $cfg }
        } catch { }
    }
}

try { Set-Clipboard -Value $Root } catch { }
Say ""
Say ("  " + (L "接下来：" "Next:")) "Cyan"
if ($wrote.Count -gt 0) {
    Say ("    1. " + (L "后端位置已经填进 Posegraph 的插件设置（原设置备份成 Posegraph.json.before-installer.bak）。" "The backend location is set in Posegraph's plugin settings (old settings backed up as Posegraph.json.before-installer.bak).")) "White"
    Say ("    2. " + (L "进游戏，/posegraph 打开 Posegraph，点右上角电源按钮启动后端。" "In game, open Posegraph with /posegraph and click the power button at the top right.")) "White"
} else {
    if ($gameRunning -and -not $SkipPluginConfig -and $pluginConfigs) { Say ("    " + (L "游戏开着，没有自动改插件设置。" "The game is running, so the plugin settings weren't changed.")) "DarkYellow" }
    Say ("    1. " + (L "进游戏，输入 /posegraph 打开 Posegraph，切到「设置」页" "In game, type /posegraph and open the Settings tab")) "White"
    Say ("    2. " + (L "点「选择一键包目录…」，选这个文件夹（路径已复制，可以直接粘贴）：" "Click 'Choose backend package folder…' and pick this folder (path copied to the clipboard):")) "White"
    Say ("       $Root") "Yellow"
    Say ("    3. " + (L "点右上角电源按钮启动后端。" "Click the power button at the top right to start the backend.")) "White"
}
Say ("       " + (L "以后更新：插件里点「更新后端」，或者再运行这个安装脚本（会记得这个位置）。不想要了：直接删掉这个文件夹。" "Later updates: 'Update backend' in the plugin, or run this installer again (it remembers this folder). To uninstall, delete the folder.")) "DarkGray"
Quit 0
