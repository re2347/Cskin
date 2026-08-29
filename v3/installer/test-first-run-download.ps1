[CmdletBinding()]
param(
    [int]$SkinId = 110003,
    [string]$UntrustedCachePath = "",
    [switch]$ForceSupabaseFailure,
    [Parameter(Mandatory)]
    [string]$AuthorizationRoot
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$objectRoot = (Resolve-Path (Join-Path $projectRoot "obj")).Path
$assemblyPath = Join-Path $projectRoot "bin\Release\net8.0-windows\PortableCskin.dll"
$testRoot = Join-Path $objectRoot ("first-run-index-" + [Guid]::NewGuid().ToString("N"))
$previousDataRoot = $env:CSKIN_DATA_ROOT
$repository = $null
$cancellation = $null
$syncTask = $null

if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "请先构建 Release：$assemblyPath"
}
if (-not $testRoot.StartsWith(
        $objectRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "测试目录不在 obj 下：$testRoot"
}

try {
    $assetRoot = Join-Path $testRoot "Assets"
    $engineRoot = Join-Path $testRoot "Engine"
    New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $engineRoot -Force | Out-Null
    foreach ($assetName in @("skin_index.json", "skin_names_zh_CN.json", "champions_zh_CN.json")) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "Assets\$assetName") `
            -Destination (Join-Path $assetRoot $assetName) `
            -Force
    }
    $authorizationSource = (Resolve-Path -LiteralPath $AuthorizationRoot).Path
    $authorizationTarget = Join-Path $testRoot "authorization"
    New-Item -ItemType Directory -Path $authorizationTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $authorizationSource "lease.bin") -Destination $authorizationTarget -Force

    $env:CSKIN_DATA_ROOT = $testRoot
    Add-Type -Path $assemblyPath
    if ($ForceSupabaseFailure) {
        $repositoryType = [CskinNative.Services.SkinRepository]
        $endpointField = $repositoryType.GetField(
            "ResourceEndpoints",
            [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static)
        if ($null -eq $endpointField) {
            throw "找不到资源端点字段，无法执行故障切换测试。"
        }
        $endpoints = $endpointField.GetValue($null)
        $endpointType = $endpoints.GetType().GetElementType()
        $constructor = $endpointType.GetConstructors(
            [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance) |
            Select-Object -First 1
        $unreachable = $constructor.Invoke(@("supabase", [Uri]"https://127.0.0.1:1/"))
        $endpoints.SetValue($unreachable, 0)
    }
    $repository = [CskinNative.Services.SkinRepository]::new($engineRoot)
    $cancellation = [Threading.CancellationTokenSource]::new()

    $indexClock = [Diagnostics.Stopwatch]::StartNew()
    $indexReady = $repository.EnsureIndexReadyAsync($cancellation.Token).GetAwaiter().GetResult()
    $indexClock.Stop()
    if (-not $indexReady -or -not $repository.IsReady) {
        throw "随包索引未在 10 秒内就绪。"
    }

    if (-not [string]::IsNullOrWhiteSpace($UntrustedCachePath)) {
        $seed = (Resolve-Path -LiteralPath $UntrustedCachePath).Path
        $relative = $repository.SkinPaths[$SkinId]
        if ([string]::IsNullOrWhiteSpace($relative)) {
            throw "随包索引没有测试皮肤路径：$SkinId"
        }
        $seedTarget = Join-Path $engineRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $seedTarget) -Force | Out-Null
        Copy-Item -LiteralPath $seed -Destination $seedTarget -Force
    }

    # V3 synchronizes the private index through Supabase/Cloudflare; no local clone starts.
    $syncClock = [Diagnostics.Stopwatch]::StartNew()
    $syncTask = $repository.SyncAsync($null, $cancellation.Token)
    $syncOk = $syncTask.GetAwaiter().GetResult()
    $syncClock.Stop()
    if (-not $syncOk) {
        throw "私有资源索引同步失败。"
    }

    $downloadClock = [Diagnostics.Stopwatch]::StartNew()
    $cached = $repository.EnsureSkinCachedAsync($SkinId, $cancellation.Token).GetAwaiter().GetResult()
    $downloadClock.Stop()
    if ([string]::IsNullOrWhiteSpace($cached) -or -not (Test-Path -LiteralPath $cached)) {
        $diagnosticLog = Join-Path $testRoot "logs\application.log"
        $diagnostic = if (Test-Path -LiteralPath $diagnosticLog) {
            (Get-Content -LiteralPath $diagnosticLog -Tail 30) -join [Environment]::NewLine
        } else {
            "<application.log missing>"
        }
        throw "首次启动没有下载皮肤 $SkinId。$([Environment]::NewLine)$diagnostic"
    }

    $bytes = [IO.File]::ReadAllBytes($cached)
    if ($bytes.Length -lt 2 -or $bytes[0] -ne 80 -or $bytes[1] -ne 75) {
        throw "下载文件不是有效的 ZIP/fantome：$cached"
    }
    $applicationLog = Get-Content -Raw -LiteralPath (Join-Path $testRoot "logs\application.log")
    if ($applicationLog -notmatch "私有皮肤流式下载完成") {
        throw "首次同步期间没有使用私有资源网关下载。"
    }
    $markerPath = $cached + ".source.json"
    if (-not (Test-Path -LiteralPath $markerPath)) {
        throw "皮肤缓存没有生成来源标记：$markerPath"
    }
    $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
    if ($marker.schema -ne "private-gateway-v2") {
        throw "皮肤缓存来源标记版本错误：$($marker.schema)"
    }
    if ($repository.GetCachedSkinPath($SkinId) -ne $cached) {
        throw "下载后的皮肤没有通过来源、大小和 SHA-256 缓存校验。"
    }
    if ($marker.source -ne "private-gitcode" -or $marker.upstream -ne "gitcode") {
        throw "皮肤未使用 privateskin GitCode 资源：source=$($marker.source) upstream=$($marker.upstream)"
    }
    $expectedGateway = if ($ForceSupabaseFailure) { "cloudflare" } else { "supabase" }
    if ($marker.gateway -ne $expectedGateway) {
        throw "资源故障切换结果错误：gateway=$($marker.gateway) expected=$expectedGateway"
    }
    if ($ForceSupabaseFailure -and $applicationLog -notmatch "endpoint=127.0.0.1") {
        throw "故障注入日志中没有 Supabase 主通道失败记录。"
    }

    [PSCustomObject]@{
        TestRoot = $testRoot
        IndexReadySeconds = [math]::Round($indexClock.Elapsed.TotalSeconds, 3)
        GatewaySyncSeconds = [math]::Round($syncClock.Elapsed.TotalSeconds, 3)
        RemoteSkinCount = $repository.RemoteSkinCount
        DownloadedPath = $cached
        DownloadedBytes = $bytes.Length
        DownloadSeconds = [math]::Round($downloadClock.Elapsed.TotalSeconds, 3)
        CacheSource = $marker.source
        Gateway = $marker.gateway
        Upstream = $marker.upstream
        Revision = $marker.revision
        UsedPrivateGateway = $true
        ForcedSupabaseFailure = [bool]$ForceSupabaseFailure
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
    if ($null -ne $syncTask) {
        try { $syncTask.GetAwaiter().GetResult() } catch { }
    }
    if ($null -ne $cancellation) {
        $cancellation.Dispose()
    }
    $env:CSKIN_DATA_ROOT = $previousDataRoot
    Start-Sleep -Milliseconds 300
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
