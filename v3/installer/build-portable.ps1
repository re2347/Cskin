[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputDir = Join-Path $projectRoot "PortableCskin"
$archivePath = Join-Path $projectRoot "PortableCskin.zip"
$stagingDir = Join-Path $projectRoot "obj\portable-publish"
$archiveOutputPath = $archivePath

& (Join-Path $PSScriptRoot "build-engine.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "引擎构建失败，退出码：$LASTEXITCODE"
}

if (Test-Path -LiteralPath $outputDir) {
    try {
        Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction Stop
    }
    catch {
        $suffix = Get-Date -Format "yyyyMMdd-HHmmss"
        $outputDir = Join-Path $projectRoot ("PortableCskin-" + $suffix)
        $archivePath = Join-Path $projectRoot ("PortableCskin-" + $suffix + ".zip")
        $archiveOutputPath = $archivePath
        Write-Warning ("旧便携目录被运行中的覆盖层占用，改用新目录：{0}" -f $outputDir)
    }
}
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}

if (Test-Path -LiteralPath $archivePath) {
    try {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction Stop
    }
    catch {
        $archiveOutputPath = Join-Path $projectRoot ("PortableCskin-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".zip")
        Write-Warning ("旧压缩包被其他程序占用，改用新文件：{0}" -f $archiveOutputPath)
    }
}

dotnet publish (Join-Path $projectRoot "CskinNative.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:SatelliteResourceLanguages=zh-Hans `
    -p:StripSymbols=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $stagingDir

$engineDir = Join-Path $stagingDir "Engine"
if (-not (Test-Path -LiteralPath (Join-Path $engineDir "Cskin.exe"))) {
    throw "发布文件缺失：$engineDir\Cskin.exe"
}

$engineAssetsDir = Join-Path $engineDir "selector_assets"
New-Item -ItemType Directory -Path $engineAssetsDir -Force | Out-Null
foreach ($assetName in @("skin_index.json", "skin_names_zh_CN.json", "champions_zh_CN.json")) {
    $assetSource = Join-Path $stagingDir ("Assets\" + $assetName)
    if (-not (Test-Path -LiteralPath $assetSource)) {
        throw "便携版资源缺失：$assetSource"
    }
    Copy-Item -LiteralPath $assetSource -Destination (Join-Path $engineAssetsDir $assetName) -Force
}

# The compiled engine is self-contained. Keeping the source script or an
# empty data directory in the portable package only adds files a user never
# needs; runtime data is created beside the executable on first use.
Remove-Item -LiteralPath (Join-Path $engineDir "cskin_engine.py") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $engineDir "data") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $engineDir "skins") -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $stagingDir -Recurse -File -Filter *.pdb | Remove-Item -Force

$required = @(
    "PortableCskin.exe",
    "PortableCskin.dll",
    "PortableCskin.runtimeconfig.json",
    "Assets\skin_index.json",
    "Assets\skin_names_zh_CN.json",
    "Assets\champions_zh_CN.json",
    "Engine\Cskin.exe",
    "Engine\cskin_engine.pyc",
    "Engine\wmic.exe",
    "Engine\selector_assets\skin_index.json",
    "Engine\xxhash\_xxhash.cp312-win_amd64.pyd",
    "Engine\zstandard\backend_c.cp312-win_amd64.pyd",
    "Engine\tools\mod-tools.exe"
)
foreach ($relativePath in $required) {
    $path = Join-Path $stagingDir $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "便携版必要文件缺失：$relativePath"
    }
}

Move-Item -LiteralPath $stagingDir -Destination $outputDir
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $outputDir,
    $archiveOutputPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true)
$archiveCheck = [System.IO.Compression.ZipFile]::OpenRead($archiveOutputPath)
$archiveCheck.Dispose()

$files = Get-ChildItem -LiteralPath $outputDir -Recurse -File
[PSCustomObject]@{
    Folder = $outputDir
    Archive = $archiveOutputPath
    Files = $files.Count
    FolderMB = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
    ArchiveMB = [math]::Round(((Get-Item -LiteralPath $archiveOutputPath).Length / 1MB), 1)
}
