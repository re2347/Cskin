[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AuthorizationRoot
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$objectRoot = Join-Path $projectRoot "obj"
$assemblyPath = Join-Path $projectRoot "bin\Release\net8.0-windows\PortableCskin.dll"
$testRoot = Join-Path $objectRoot ("authorization-failover-" + [Guid]::NewGuid().ToString("N"))
$previousDataRoot = $env:CSKIN_DATA_ROOT
$previousProvider = $env:CSKIN_AUTH_PROVIDER

if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "请先构建 Release：$assemblyPath"
}

try {
    $authorizationSource = (Resolve-Path -LiteralPath $AuthorizationRoot).Path
    $authorizationTarget = Join-Path $testRoot "authorization"
    New-Item -ItemType Directory -Path $authorizationTarget -Force | Out-Null
    foreach ($fileName in @("device.identity", "lease.bin")) {
        $source = Join-Path $authorizationSource $fileName
        if (-not (Test-Path -LiteralPath $source)) {
            throw "授权测试文件缺失：$source"
        }
        Copy-Item -LiteralPath $source -Destination $authorizationTarget -Force
    }

    $env:CSKIN_DATA_ROOT = $testRoot
    $env:CSKIN_AUTH_PROVIDER = $null
    Add-Type -Path $assemblyPath

    $lease = [CskinNative.Services.LeaseStore]::new().Load()
    if ($null -eq $lease) {
        throw "隔离目录中的授权租约无法解密。"
    }

    $endpointField = [CskinNative.Services.AuthorizationClient].GetField(
        "_endpoints",
        [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance)
    if ($null -eq $endpointField) {
        throw "找不到授权端点字段，无法注入故障。"
    }

    # A connection failure on the primary provider must advance exactly once
    # to the healthy Cloudflare custom-domain endpoint.
    $validClient = [CskinNative.Services.AuthorizationClient]::new()
    try {
        $validEndpoints = $endpointField.GetValue($validClient)
        $validEndpoints[0] = [Uri]"https://127.0.0.1:1/"
        $validEndpoints[1] = [Uri]"https://license.re2347.ccwu.cc/"
        if ($validEndpoints.Count -gt 2) {
            $validEndpoints[2] = [Uri]"https://127.0.0.1:1/"
        }

        $validClock = [Diagnostics.Stopwatch]::StartNew()
        $device = [CskinNative.Services.DeviceIdentity]::LoadOrCreate()
        try {
            $validResult = $validClient.VerifyAsync($lease, $device, "0.3.0").GetAwaiter().GetResult()
        }
        finally {
            $device.Dispose()
        }
        $validClock.Stop()

        if (-not $validResult.Succeeded -or $null -eq $validResult.Value) {
            throw "主端点故障后没有通过 Cloudflare 恢复：$($validResult.ErrorCode) $($validResult.ErrorMessage)"
        }
    }
    finally {
        $validClient.Dispose()
    }

    # A signed request carrying a bad lease token must receive a terminal
    # authorization error. The unreachable fallback entries make an accidental
    # retry observable as AUTH_UNREACHABLE/AUTH_TIMEOUT instead.
    $invalidLease = [CskinNative.Services.AuthorizationLease]::new()
    foreach ($property in $lease.PSObject.Properties) {
        $invalidLease.($property.Name) = $property.Value
    }
    $invalidLease.LeaseToken = $lease.LeaseToken + "-invalid"

    $terminalClient = [CskinNative.Services.AuthorizationClient]::new()
    try {
        $terminalEndpoints = $endpointField.GetValue($terminalClient)
        for ($index = 1; $index -lt $terminalEndpoints.Count; $index++) {
            $terminalEndpoints[$index] = [Uri]"https://127.0.0.1:1/"
        }

        $terminalClock = [Diagnostics.Stopwatch]::StartNew()
        $device = [CskinNative.Services.DeviceIdentity]::LoadOrCreate()
        try {
            $terminalResult = $terminalClient.VerifyAsync($invalidLease, $device, "0.3.0").GetAwaiter().GetResult()
        }
        finally {
            $device.Dispose()
        }
        $terminalClock.Stop()

        if ($terminalResult.Succeeded) {
            throw "无效租约被错误接受。"
        }
        if ([int]$terminalResult.StatusCode -notin @(401, 403)) {
            throw "401/403 被错误切换到后备端点：status=$([int]$terminalResult.StatusCode) code=$($terminalResult.ErrorCode)"
        }
    }
    finally {
        $terminalClient.Dispose()
    }

    [PSCustomObject]@{
        ValidFailover = "supabase-unreachable -> cloudflare-ok"
        ValidSeconds = [math]::Round($validClock.Elapsed.TotalSeconds, 3)
        TerminalError = $terminalResult.ErrorCode
        TerminalStatus = [int]$terminalResult.StatusCode
        TerminalSeconds = [math]::Round($terminalClock.Elapsed.TotalSeconds, 3)
        Result = "ok"
    } | Format-List
}
finally {
    $env:CSKIN_DATA_ROOT = $previousDataRoot
    $env:CSKIN_AUTH_PROVIDER = $previousProvider
    Start-Sleep -Milliseconds 200
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
