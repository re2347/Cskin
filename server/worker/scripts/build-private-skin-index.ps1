[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepositoryRoot,

    [string]$OutputPath,

    [string]$RepositoryLabel = "privateskin",

    [string]$Ref = "main"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $root ".git"))) {
    throw "不是 Git 工作区：$root"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "resources\worker_skin_index.json"
}

$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') {
    throw "无法读取仓库提交版本"
}

$names = @{}
$namePath = Join-Path $root "resources\zh\skin_ids.json"
if (Test-Path -LiteralPath $namePath) {
    $names = Get-Content -LiteralPath $namePath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
}

$itemsById = @{}
$treePaths = & git -C $root ls-tree -r --name-only HEAD -- skins
if ($LASTEXITCODE -ne 0) {
    throw "无法读取仓库皮肤目录"
}
foreach ($pathValue in $treePaths) {
    $path = ([string]$pathValue).Replace('\', '/')
    if ($path -notmatch '^skins/\d+/\d+/(?:\d+/)?(?<id>\d+)\.fantome$') {
        continue
    }
    $skinId = [int]$Matches.id
    $name = if ($names.ContainsKey([string]$skinId)) { [string]$names[[string]$skinId] } else { "" }
    $item = [ordered]@{ id = $skinId; path = $path; name = $name }
    if (-not $itemsById.ContainsKey($skinId)) {
        $itemsById[$skinId] = $item
        continue
    }

    # A mirrored tree may retain a stale duplicate path. Prefer the path whose
    # champion directory matches Riot's skin-id prefix, which is also how the
    # desktop catalog resolves champion ownership.
    $expectedChampionId = [int][Math]::Floor($skinId / 1000)
    $newChampionId = [int]($path.Split('/')[1])
    $oldChampionId = [int](([string]$itemsById[$skinId].path).Split('/')[1])
    if ($newChampionId -eq $expectedChampionId -and $oldChampionId -ne $expectedChampionId) {
        $itemsById[$skinId] = $item
    }
}
$items = @($itemsById.Values)
if ($items.Count -eq 0) {
    throw "仓库中没有找到 .fantome 皮肤"
}

$payload = [ordered]@{
    schema = 1
    repository = $RepositoryLabel
    ref = $Ref
    revision = $revision
    generatedAt = [DateTime]::UtcNow.ToString("O")
    skins = @($items | Sort-Object { $_.id }, { $_.path })
}
$target = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
$json = $payload | ConvertTo-Json -Depth 5 -Compress
[System.IO.File]::WriteAllText($target, $json, [System.Text.UTF8Encoding]::new($false))

[PSCustomObject]@{
    Output = $target
    Revision = $revision
    Skins = $items.Count
    Bytes = (Get-Item -LiteralPath $target).Length
}
