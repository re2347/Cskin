[CmdletBinding()]
param(
    [string]$AssemblyPath = "",
    [int]$SkinId = 22044
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $projectRoot "bin\Release\net8.0-windows\PortableCskin.dll"
}
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$testRoot = Join-Path (Join-Path $projectRoot "obj") ("preview-fallback-" + [Guid]::NewGuid().ToString("N"))
$previousDataRoot = $env:CSKIN_DATA_ROOT
$image = $null

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $env:CSKIN_DATA_ROOT = $testRoot
    Add-Type -Path $AssemblyPath

    # Force CommunityDragon to fail so the test exercises Wiki lookup and the
    # deterministic base-skin fallback without changing production settings.
    $skin = [CskinNative.Services.Skin]::new()
    $skin.Id = $SkinId
    $skin.Name = "海之歌 艾希（红宝石）"
    $skin.EnglishName = "Ocean Song Ashe (Ruby)"
    $skin.Slug = "Ashe"
    $skin.Chroma = $true
    $skin.BaseSkinId = 22043
    $skin.Image = "https://127.0.0.1:1/invalid.png"
    $skin.FallbackImage = "https://ddragon.leagueoflegends.com/cdn/img/champion/splash/Ashe_43.jpg"
    $image = [CskinNative.Services.PreviewImageResolver]::LoadAsync($skin).GetAwaiter().GetResult()
    if ($null -eq $image) {
        throw "炫彩预览图回退未返回基础皮肤图片。"
    }
    $image.Dispose()
    $image = $null

    $logPath = Join-Path $testRoot "logs\application.log"
    $log = if (Test-Path -LiteralPath $logPath) { Get-Content -Raw -LiteralPath $logPath } else { "" }
    if ($log -notmatch "Wiki 预览图搜索失败" -or $log -notmatch "预览图回退基础皮肤") {
        throw "预览回退日志不完整：$logPath"
    }

    # Verify that a remote revision can merge localized and English metadata
    # into a skin that was absent from the bundled catalog.
    $catalog = [CskinNative.Services.LocalCatalog]::new()
    $champion = [CskinNative.Services.Champion]::new()
    $champion.Id = 22
    $champion.Name = "寒冰射手"
    $champion.Slug = "Ashe"
    $champion.Skins = [System.Collections.Generic.List[CskinNative.Services.Skin]]::new()
    $catalog.Champions.Add($champion)
    $paths = [System.Collections.Generic.Dictionary[int,string]]::new()
    $paths.Add($SkinId, ("skins/22/22043/{0}/{0}.fantome" -f $SkinId))
    $names = [System.Collections.Generic.Dictionary[int,string]]::new()
    $names.Add($SkinId, "海之歌 艾希（红宝石）")
    $englishNames = [System.Collections.Generic.Dictionary[int,string]]::new()
    $englishNames.Add($SkinId, "Ocean Song Ashe (Ruby)")
    $merged = $catalog.MergeRepository($paths, $names, $englishNames, $null)
    $mergedSkin = $champion.Skins | Where-Object Id -eq $SkinId | Select-Object -First 1
    $mergeOk = $merged -ge 1 -and $null -ne $mergedSkin -and
        $mergedSkin.LocalizedName -eq "海之歌 艾希（红宝石）" -and
        $mergedSkin.EnglishName -eq "Ocean Song Ashe (Ruby)"
    if (-not $mergeOk) {
        throw "revision 合并没有保留中英文名称。"
    }

    # Verify the real CommunityDragon chroma preview path used by newly
    # published skins. The path is case-sensitive and must use the lower-case
    # champion directory plus the per-chroma skin slot.
    $jinx = [CskinNative.Services.Skin]::new()
    $jinx.Id = 222070
    $jinx.Name = "海之歌 金克丝 清凉海风"
    $jinx.EnglishName = "Ocean Song Jinx (Sapphire)"
    $jinx.Slug = "Jinx"
    $jinx.Chroma = $true
    $jinx.BaseSkinId = 222065
    $jinx.Image = "https://127.0.0.1:1/invalid-chroma.png"
    $jinx.FallbackImage = "https://127.0.0.1:1/invalid-base.jpg"
    $jinxImage = [CskinNative.Services.PreviewImageResolver]::LoadAsync($jinx).GetAwaiter().GetResult()
    if ($null -eq $jinxImage) {
        throw "CommunityDragon 炫彩原始预览图解析失败。"
    }
    $jinxImage.Dispose()
    $jinxImage = $null
    $jinxLog = if (Test-Path -LiteralPath $logPath) { Get-Content -Raw -LiteralPath $logPath } else { "" }
    if ($jinxLog -notmatch "source=communitydragon-chromapreview") {
        throw "CommunityDragon 炫彩预览图命中日志缺失：$logPath"
    }

    $movedPaths = [System.Collections.Generic.Dictionary[int,string]]::new()
    $movedPaths.Add($SkinId, ("skins/22/22052/{0}/{0}.fantome" -f $SkinId))
    $catalog.MergeRepository($movedPaths, $names, $englishNames, $null) | Out-Null
    if ($mergedSkin.BaseSkinId -ne 22052 -or $mergedSkin.ChampionId -ne 22) {
        throw "revision 合并没有更新皮肤路径对应的基础皮肤归属。"
    }

    [PSCustomObject]@{
        Assembly = $AssemblyPath
        WikiOrBaseFallback = $true
        RevisionNameMerge = $true
        RevisionPathMerge = $true
        SkinId = $SkinId
        LogPath = $logPath
        Result = "ok"
    } | Format-List
}
finally {
    if ($null -ne $image) {
        $image.Dispose()
    }
    if ($null -ne $jinxImage) {
        $jinxImage.Dispose()
    }
    $env:CSKIN_DATA_ROOT = $previousDataRoot
    Start-Sleep -Milliseconds 200
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
