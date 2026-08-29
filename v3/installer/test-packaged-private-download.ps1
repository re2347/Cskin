[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,
    [Parameter(Mandatory)]
    [string]$AuthorizationRoot,
    [int]$SkinId = 86044
)

$ErrorActionPreference = "Stop"
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$authorizationSource = (Resolve-Path -LiteralPath $AuthorizationRoot).Path
$assemblyPath = Join-Path $package "PortableCskin.dll"
$engineRoot = Join-Path $package "Engine"
$previousDataRoot = $env:CSKIN_DATA_ROOT
$repository = $null
$cancellation = $null

foreach ($required in @(
    $assemblyPath,
    (Join-Path $package "Assets\skin_index.json"),
    (Join-Path $engineRoot "Cskin.exe"),
    (Join-Path $engineRoot "tools\mod-tools.exe"))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "发行包必要文件缺失：$required"
    }
}

$existingSkins = Get-ChildItem -LiteralPath (Join-Path $engineRoot "skins") `
    -Filter "*.fantome" -File -Recurse -ErrorAction SilentlyContinue
if (@($existingSkins).Count -gt 0) {
    throw "测试目录不是干净解压状态，已经存在皮肤缓存。"
}
foreach ($unexpectedAuthorization in @("lease.bin", "remembered-license.bin")) {
    if (Test-Path -LiteralPath (Join-Path $package "authorization\$unexpectedAuthorization")) {
        throw "测试目录不是未激活的首次启动状态，已经存在：$unexpectedAuthorization"
    }
}

try {
    $authorizationTarget = Join-Path $package "authorization"
    New-Item -ItemType Directory -Path $authorizationTarget -Force | Out-Null
    foreach ($fileName in @("device.identity", "lease.bin")) {
        $source = Join-Path $authorizationSource $fileName
        if (-not (Test-Path -LiteralPath $source)) {
            throw "授权测试文件缺失：$source"
        }
        Copy-Item -LiteralPath $source -Destination $authorizationTarget -Force
    }

    $env:CSKIN_DATA_ROOT = $package
    Add-Type -Path $assemblyPath
    [CskinNative.Services.AppPaths]::EnsureRuntime()

    $lease = [CskinNative.Services.LeaseStore]::new().Load()
    if ($null -eq $lease) {
        throw "发行目录中的隔离授权无法解密。"
    }

    $repository = [CskinNative.Services.SkinRepository]::new($engineRoot)
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(4))

    $indexClock = [Diagnostics.Stopwatch]::StartNew()
    $indexReady = $repository.EnsureIndexReadyAsync($cancellation.Token).GetAwaiter().GetResult()
    $syncReady = $repository.SyncAsync($null, $cancellation.Token).GetAwaiter().GetResult()
    $indexClock.Stop()
    if (-not $indexReady -or -not $syncReady -or -not $repository.IsReady) {
        throw "发行包索引没有就绪。"
    }

    $downloadClock = [Diagnostics.Stopwatch]::StartNew()
    $cached = $repository.EnsureSkinCachedAsync($SkinId, $cancellation.Token).GetAwaiter().GetResult()
    $downloadClock.Stop()
    if ([string]::IsNullOrWhiteSpace($cached) -or -not (Test-Path -LiteralPath $cached)) {
        throw "发行包没有下载测试皮肤：$SkinId"
    }

    $expectedSkinRoot = [IO.Path]::GetFullPath((Join-Path $engineRoot "skins")) + [IO.Path]::DirectorySeparatorChar
    $resolvedCached = [IO.Path]::GetFullPath($cached)
    if (-not $resolvedCached.StartsWith($expectedSkinRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "皮肤缓存落在发行目录外：$resolvedCached"
    }

    $markerPath = $cached + ".source.json"
    if (-not (Test-Path -LiteralPath $markerPath)) {
        throw "皮肤缓存缺少来源标记：$markerPath"
    }
    $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
    if ($marker.schema -ne "private-gateway-v2" -or
        $marker.source -ne "private-gitcode" -or
        $marker.upstream -ne "gitcode" -or
        [string]::IsNullOrWhiteSpace($marker.revision) -or
        [string]::IsNullOrWhiteSpace($marker.sha256)) {
        throw "发行包缓存来源标记无效。"
    }
    if ($repository.GetCachedSkinPath($SkinId) -ne $cached) {
        throw "发行包下载后的皮肤没有通过二次缓存校验。"
    }

    [PSCustomObject]@{
        PackageRoot = $package
        CleanExtract = $true
        RemoteSkinCount = $repository.RemoteSkinCount
        SkinId = $SkinId
        DownloadedPath = $cached
        DownloadedBytes = (Get-Item -LiteralPath $cached).Length
        Gateway = $marker.gateway
        Upstream = $marker.upstream
        Revision = $marker.revision
        IndexSeconds = [math]::Round($indexClock.Elapsed.TotalSeconds, 3)
        DownloadSeconds = [math]::Round($downloadClock.Elapsed.TotalSeconds, 3)
        Result = "ok"
    } | Format-List
}
finally {
    if ($null -ne $cancellation) {
        $cancellation.Cancel()
    }
    if ($null -ne $repository) {
        $repository.Stop()
    }
    if ($null -ne $cancellation) {
        $cancellation.Dispose()
    }
    $env:CSKIN_DATA_ROOT = $previousDataRoot
}
