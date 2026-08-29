[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [string]$FixturePath,
    [int]$SkinId = 804005,
    [int]$TargetSkinId = 804000,
    [string]$FixtureRelativePath = "804\804001\804005\804005.fantome",
    [string]$ExpectedRemovedGlobalWad = "",
    [string]$ExpectedChampionRepair = "",
    [switch]$ExpectedImportedFantome,
    [switch]$ExpectedHudAlias,
    [string]$ExpectedOverlayWad = "",
    [int]$ExpectedOverlayWadCount = -1,
    [switch]$KeepFailedTestRoot,
    [string]$GameDirectory = "C:\wegameapps\英雄联盟\Game"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
if ([string]::IsNullOrWhiteSpace($FixturePath)) {
    $FixturePath = Join-Path $projectRoot "PortableCskin\Engine\skins\804\804001\804005\804005.fantome"
}
$FixturePath = (Resolve-Path -LiteralPath $FixturePath).Path
$GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path

if (Get-Process -Name "League of Legends" -ErrorAction SilentlyContinue) {
    throw "League of Legends 正在运行，跳过隔离注入测试。"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory "League of Legends.exe"))) {
    throw "游戏目录无效：$GameDirectory"
}

$testDrive = if (Test-Path -LiteralPath 'D:\') { 'D:\' } else { [IO.Path]::GetTempPath() }
$testBase = Join-Path $testDrive "CskinTests"
New-Item -ItemType Directory -Path $testBase -Force | Out-Null
$testBase = (Resolve-Path -LiteralPath $testBase).Path
$testRoot = Join-Path $testBase ("frozen-fast-apply-" + [Guid]::NewGuid().ToString("N"))
$engineRoot = Join-Path $testRoot "Engine"
$engineProcess = $null
$runnerProcessIds = @()
$previousGameDirectory = $env:AATROX_GAME_DIR
$testSucceeded = $false

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageRoot "Engine") -Destination $engineRoot -Recurse

    $skinTarget = Join-Path $engineRoot ("skins\" + $FixtureRelativePath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $skinTarget) -Force | Out-Null
    Copy-Item -LiteralPath $FixturePath -Destination $skinTarget -Force
    Remove-Item -LiteralPath (Join-Path $engineRoot "data") -Recurse -Force -ErrorAction SilentlyContinue

    $env:AATROX_GAME_DIR = $GameDirectory
    $engineProcess = Start-Process -FilePath (Join-Path $engineRoot "Cskin.exe") `
        -ArgumentList "--no-browser" `
        -WorkingDirectory $engineRoot `
        -WindowStyle Hidden `
        -PassThru

    $selectorLog = Join-Path $engineRoot "data\selector.log"
    $port = $null
    foreach ($attempt in 1..40) {
        Start-Sleep -Milliseconds 250
        if (-not (Test-Path -LiteralPath $selectorLog)) { continue }
        $match = [regex]::Match(
            (Get-Content -Raw -LiteralPath $selectorLog),
            'Selector ready at http://127\.0\.0\.1:(\d+)/')
        if ($match.Success) {
            $port = [int]$match.Groups[1].Value
            break
        }
    }
    if ($null -eq $port) {
        throw "冻结引擎未能启动本地 API。"
    }

    Invoke-RestMethod -Method Post `
        -Uri ("http://127.0.0.1:{0}/api/rebuild" -f $port) `
        -ContentType "application/json" `
        -Body "{}" `
        -TimeoutSec 5 | Out-Null

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-RestMethod -Method Post `
        -Uri ("http://127.0.0.1:{0}/api/apply" -f $port) `
        -ContentType "application/json" `
        -Body (@{ skinId = $SkinId; targetSkinId = $TargetSkinId } | ConvertTo-Json -Compress) `
        -TimeoutSec 20
    $stopwatch.Stop()

    $engineLog = Get-Content -Raw -LiteralPath $selectorLog
    foreach ($attempt in 1..10) {
        if ($engineLog -match "runoverlay background monitor started") { break }
        Start-Sleep -Milliseconds 200
        $engineLog = Get-Content -Raw -LiteralPath $selectorLog
    }
    $runnerProcessIds = [regex]::Matches($engineLog, 'runoverlay status: pid=(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } |
        Select-Object -Unique

    if ($stopwatch.Elapsed.TotalSeconds -ge 10) {
        throw "冻结引擎应用响应仍然阻塞：$($stopwatch.Elapsed.TotalSeconds) 秒"
    }
    if ($response.injectionStatus -ne "waiting-game") {
        throw "冻结引擎没有返回 waiting-game：$($response.injectionStatus)"
    }
    if ($engineLog -notmatch "runoverlay background monitor started") {
        throw "冻结引擎没有启动后台注入监控。"
    }
    if ($engineLog -match "runoverlay confirmation started") {
        throw "冻结引擎仍包含旧版同步等待逻辑。"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedRemovedGlobalWad) -and
        $engineLog -notmatch ("Compatibility filter removed incompatible global WAD:.*" + [regex]::Escape($ExpectedRemovedGlobalWad))) {
        throw "冻结引擎没有过滤预期的全局 WAD：$ExpectedRemovedGlobalWad"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedChampionRepair) -and
        $engineLog -notmatch ("Compatibility repair pruned incompatible WAD entries:.*" + [regex]::Escape($ExpectedChampionRepair))) {
        throw "冻结引擎没有修复预期的 WAD：$ExpectedChampionRepair"
    }
    if ($ExpectedImportedFantome -and $engineLog -notmatch "Expanded package imported:") {
        throw "冻结引擎没有通过 mod-tools 导入展开式 fantome。"
    }
    if ($ExpectedImportedFantome -and $engineLog -notmatch "Imported package slot mapping completed:") {
        throw "冻结引擎没有映射导入包的皮肤槽位。"
    }
    if ($ExpectedHudAlias -and $engineLog -notmatch "Expanded HUD slot mapping completed:") {
        throw "冻结引擎没有映射展开式皮肤的 HUD 资源。"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedOverlayWad) -and
        $engineLog -notmatch ("wadFiles=.*" + [regex]::Escape($ExpectedOverlayWad))) {
        throw "冻结引擎没有生成预期的覆盖 WAD：$ExpectedOverlayWad"
    }
    if ($ExpectedOverlayWadCount -ge 0 -and $response.overlayWadCount -ne $ExpectedOverlayWadCount) {
        throw "冻结引擎生成的覆盖 WAD 数量不符：actual=$($response.overlayWadCount) expected=$ExpectedOverlayWadCount"
    }

    [PSCustomObject]@{
        Package = $PackageRoot
        ElapsedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        Result = "ok"
        InjectionStatus = $response.injectionStatus
        OverlayWadCount = $response.overlayWadCount
        RemovedGlobalWad = $ExpectedRemovedGlobalWad
        ChampionRepair = $ExpectedChampionRepair
        ImportedFantome = [bool]$ExpectedImportedFantome
        HudAlias = [bool]$ExpectedHudAlias
        BackgroundMonitor = $true
        OldBlockingConfirmation = $false
    } | Format-List
    $testSucceeded = $true
}
finally {
    foreach ($runnerProcessId in $runnerProcessIds) {
        Stop-Process -Id $runnerProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $engineProcess) {
        Stop-Process -Id $engineProcess.Id -Force -ErrorAction SilentlyContinue
        $engineProcess.Dispose()
    }
    $env:AATROX_GAME_DIR = $previousGameDirectory
    Start-Sleep -Milliseconds 300

    $cleanupRoot = $testBase + [IO.Path]::DirectorySeparatorChar
    if (($testSucceeded -or -not $KeepFailedTestRoot) -and
        $testRoot.StartsWith($cleanupRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    } elseif ($KeepFailedTestRoot -and (Test-Path -LiteralPath $testRoot)) {
        Write-Warning "保留失败测试目录：$testRoot"
    }
}
