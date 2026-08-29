param(
  [string]$BaseUrl = "https://license.re2347.ccwu.cc",
  [string]$AdminKey = ""
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd('/')
$health = Invoke-WebRequest -Uri "$base/health" -UseBasicParsing -SkipHttpErrorCheck
Write-Host "health: $($health.StatusCode) $($health.Content)"

if ($AdminKey) {
  $headers = @{ "X-Admin-Key" = $AdminKey }
  $stats = Invoke-WebRequest -Uri "$base/v1/admin/stats" -Headers $headers -UseBasicParsing -SkipHttpErrorCheck
  Write-Host "admin stats: $($stats.StatusCode) $($stats.Content)"
}
