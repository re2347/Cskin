[CmdletBinding()]
param(
    [string]$PackageRoot,
    [ValidateSet("all", "engine", "application", "authorization")]
    [string]$Check = "all"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $projectRoot "PortableCskin"
}
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$engineTestRoot = Join-Path $projectRoot "obj\portable-engine-check"

function Assert-Exists {
    param([string]$RelativePath)
    if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $RelativePath))) {
        throw "便携包缺少必要文件：$RelativePath"
    }
}

foreach ($file in @(
    "PortableCskin.exe",
    "PortableCskin.dll",
    "Assets\skin_index.json",
    "Engine\Cskin.exe",
    "Engine\cskin_engine.pyc",
    "Engine\wmic.exe",
    "Engine\selector_assets\skin_index.json",
    "Engine\tools\mod-tools.exe")) {
    Assert-Exists $file
}

if (-not (Get-ChildItem -LiteralPath (Join-Path $PackageRoot "Engine\xxhash") -Filter "*.pyd" -File -ErrorAction SilentlyContinue)) {
    throw "便携包缺少 xxhash 原生模块。"
}
if (-not (Get-ChildItem -LiteralPath (Join-Path $PackageRoot "Engine\zstandard") -Filter "*.pyd" -File -ErrorAction SilentlyContinue)) {
    throw "便携包缺少 zstandard 原生模块。"
}
if (Test-Path -LiteralPath (Join-Path $PackageRoot "Engine\skins")) {
    $bundledSkins = Get-ChildItem -LiteralPath (Join-Path $PackageRoot "Engine\skins") -Recurse -Filter "*.fantome" -File -ErrorAction SilentlyContinue
    if (@($bundledSkins).Count -gt 0) {
        throw "便携包不应内置 skins；皮肤应在授权后通过 Worker 按需下载。"
    }
}

if (Test-Path -LiteralPath (Join-Path $PackageRoot "Tools\Git")) {
    throw "V3 便携包不应再包含 Git 运行时。"
}

if ($Check -in @("all", "engine")) {
if (Test-Path -LiteralPath $engineTestRoot) {
    Remove-Item -LiteralPath $engineTestRoot -Recurse -Force
}
    New-Item -ItemType Directory -Path $engineTestRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageRoot "Engine") -Destination $engineTestRoot -Recurse
    New-Item -ItemType Directory -Path (Join-Path $engineTestRoot "Engine\selector_assets") -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PackageRoot "Assets") -File | Copy-Item -Destination (Join-Path $engineTestRoot "Engine\selector_assets") -Force

$engineProcess = $null
try {
    $enginePath = Join-Path $engineTestRoot "Engine\Cskin.exe"
    $engineProcess = Start-Process -FilePath $enginePath `
        -ArgumentList "--no-browser" `
        -WorkingDirectory (Split-Path -Parent $enginePath) `
        -WindowStyle Hidden `
        -PassThru

        $health = $null
        foreach ($attempt in 1..40) {
            Start-Sleep -Milliseconds 250
            $logPath = Join-Path (Split-Path -Parent $enginePath) "data\selector.log"
            if (Test-Path -LiteralPath $logPath) {
                $ports = [regex]::Matches((Get-Content -Raw -LiteralPath $logPath), 'Selector ready at http://127\.0\.0\.1:(\d+)/') |
                    ForEach-Object { [int]$_.Groups[1].Value } | Select-Object -Last 4 -Unique
                foreach ($port in $ports) {
                    try {
                        $response = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/api/health" -f $port) -TimeoutSec 1
                        if ($response.engine -eq $true -and $response.roseEngine -eq $true) {
                            $health = $response
                            break
                        }
                    } catch { }
                }
            }
            if ($null -ne $health) { break }
        }
    if ($null -eq $health) {
        throw "便携引擎未能启动并提供本地 API。"
    }
}
finally {
    if ($null -ne $engineProcess) {
        $engineProcess.Refresh()
        if (-not $engineProcess.HasExited) {
            Stop-Process -Id $engineProcess.Id -Force
            $engineProcess.WaitForExit(3000)
        }
        $engineProcess.Dispose()
    }
    if (Test-Path -LiteralPath $engineTestRoot) {
        Remove-Item -LiteralPath $engineTestRoot -Recurse -Force
    }
}

}

if ($Check -in @("all", "application")) {
$applicationProcess = $null
try {
    $applicationProcess = Start-Process -FilePath (Join-Path $PackageRoot "PortableCskin.exe") `
        -WorkingDirectory $PackageRoot `
        -PassThru
    $windowTitle = ""
    foreach ($attempt in 1..40) {
        Start-Sleep -Milliseconds 250
        $applicationProcess.Refresh()
        $windowTitle = $applicationProcess.MainWindowTitle
        if ($windowTitle -eq "PortableCskin 授权") { break }
    }
    if ($windowTitle -ne "PortableCskin 授权") {
        throw "启动后没有出现卡密授权窗口。"
    }
}
finally {
    if ($null -ne $applicationProcess) {
        $applicationProcess.Refresh()
        if (-not $applicationProcess.HasExited) {
            Stop-Process -Id $applicationProcess.Id -Force
            $applicationProcess.WaitForExit(3000)
        }
        $applicationProcess.Dispose()
    }
}

}

if ($Check -in @("all", "authorization")) {
$authorizationHealth = Invoke-RestMethod -Uri "https://license.re2347.ccwu.cc/health" -TimeoutSec 10
if ($authorizationHealth.ok -ne $true) {
    throw "授权服务健康检查未通过。"
}

}

[PSCustomObject]@{
    Package = $PackageRoot
    Check = $Check
    GitDependency = $false
    Result = "ok"
} | Format-List
