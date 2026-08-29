[CmdletBinding()]
param(
    [string[]]$BaseUrl = @(
        "https://license.re2347.ccwu.cc",
        "https://cskin-license-staging.2469416170.workers.dev"
    ),
    [int]$TimeoutSec = 20
)

$ErrorActionPreference = "Stop"

if ($BaseUrl.Count -eq 0) {
    throw "至少提供一个授权端点"
}
if ($TimeoutSec -lt 1 -or $TimeoutSec -gt 120) {
    throw "TimeoutSec 必须在 1 到 120 秒之间"
}

$failed = 0
foreach ($rawUrl in $BaseUrl) {
    $base = $rawUrl.Trim().TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($base)) {
        Write-Host "[FAIL] 空端点"
        $failed++
        continue
    }

    try {
        $uri = [Uri]$base
        if ($uri.Scheme -ne "https" -or $uri.Query -or $uri.Fragment) {
            throw "端点必须是没有查询串和片段的 HTTPS URL"
        }

        $response = Invoke-WebRequest -Uri "$base/health" -Method Get -TimeoutSec $TimeoutSec
        $payload = $response.Content | ConvertFrom-Json
        if ($response.StatusCode -ne 200 -or $payload.ok -ne $true) {
            throw "健康检查未就绪（HTTP $($response.StatusCode)）"
        }
        if ($payload.service -ne "cskin-license-worker") {
            throw "服务标识不匹配"
        }
        if ($payload.databaseReady -ne $true -or $payload.pepperConfigured -ne $true) {
            throw "D1 或授权密钥未就绪"
        }

        Write-Host "[OK] $base (HTTP $($response.StatusCode), environment=$($payload.environment))"
    }
    catch {
        Write-Host "[FAIL] $base - $($_.Exception.Message)" -ForegroundColor Red
        $failed++
    }
}

if ($failed -gt 0) {
    throw "$failed 个授权端点健康检查失败"
}
