[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$specPath = Join-Path $projectRoot "Cskin.spec"
$sourcePath = Join-Path $projectRoot "Runtime\Cskin\cskin_engine.py"
$distRoot = Join-Path $projectRoot "dist\Cskin"
$workRoot = Join-Path $projectRoot "build"
$runtimeRoot = Join-Path $projectRoot "Runtime\Cskin"

$pyinstaller = (Get-Command pyinstaller.exe -ErrorAction Stop).Source
$python = (Get-Command python.exe -ErrorAction Stop).Source

if (-not (Test-Path -LiteralPath $specPath)) {
    throw "引擎构建 spec 不存在：$specPath"
}
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "引擎源文件不存在：$sourcePath"
}

$buildStartedUtc = [DateTime]::UtcNow
& $pyinstaller --noconfirm --clean `
    --distpath (Join-Path $projectRoot "dist") `
    --workpath $workRoot `
    $specPath
if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller 构建失败，退出码：$LASTEXITCODE"
}

& $python -m py_compile $sourcePath
if ($LASTEXITCODE -ne 0) {
    throw "引擎 Python 字节码编译失败，退出码：$LASTEXITCODE"
}

$builtEngine = Join-Path $distRoot "Cskin.exe"
if (-not (Test-Path -LiteralPath $builtEngine)) {
    throw "PyInstaller 未生成引擎：$distRoot\Cskin.exe"
}
$builtEngineInfo = Get-Item -LiteralPath $builtEngine
if ($builtEngineInfo.LastWriteTimeUtc -lt $buildStartedUtc.AddSeconds(-2)) {
    throw "PyInstaller 引擎不是本次构建产物：$builtEngine"
}
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
Get-ChildItem -LiteralPath $distRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $runtimeRoot $_.Name) -Recurse -Force
}

$compiledSource = Get-ChildItem -LiteralPath (Join-Path $runtimeRoot "__pycache__") -File -Filter "cskin_engine*.pyc" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $compiledSource) {
    throw "未找到引擎 Python 字节码：$runtimeRoot\__pycache__"
}
Copy-Item -LiteralPath $compiledSource.FullName -Destination (Join-Path $runtimeRoot "cskin_engine.pyc") -Force
Remove-Item -LiteralPath (Join-Path $runtimeRoot "__pycache__") -Recurse -Force

[PSCustomObject]@{
    Engine = Join-Path $runtimeRoot "Cskin.exe"
    EngineBytes = (Get-Item -LiteralPath (Join-Path $runtimeRoot "Cskin.exe")).Length
    EngineSha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeRoot "Cskin.exe") -Algorithm SHA256).Hash
    SourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    Bytecode = Join-Path $runtimeRoot "cskin_engine.pyc"
    BytecodeBytes = (Get-Item -LiteralPath (Join-Path $runtimeRoot "cskin_engine.pyc")).Length
} | Format-List
