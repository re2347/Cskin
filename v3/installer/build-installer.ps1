[CmdletBinding()]
param(
    # Local functional tests may be unsigned. Do not use this switch for a
    # package distributed to users.
    [switch]$UnsignedTestBuild,

    # Local signing tests use a certificate created by setup-local-signing.ps1.
    # This certificate is intentionally not suitable for distribution.
    [switch]$LocalTestBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = Join-Path $projectRoot "publish-final"
$outputDir = Join-Path $projectRoot "installer-output"
$iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source

& (Join-Path $PSScriptRoot "build-engine.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "引擎构建失败，退出码：$LASTEXITCODE"
}

if ($UnsignedTestBuild -and $LocalTestBuild) {
    throw "-UnsignedTestBuild 和 -LocalTestBuild 不能同时使用。"
}

# The installer recursively packages this directory. Publish into a clean
# output tree so a prior run cannot carry stale engine files or runtime data.
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

function Get-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $sdkRoots = @(
        (Join-Path $programFilesX86 "Windows Kits\10\bin"),
        (Join-Path $env:ProgramFiles "Windows Kits\10\bin")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }
    $sdkTool = foreach ($root in $sdkRoots) {
        Get-ChildItem -Path $root -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    }
    if ($null -eq $sdkTool) {
        throw "未找到 signtool.exe。请安装 Windows SDK，或仅在本地验证时使用 -UnsignedTestBuild。"
    }
    return $sdkTool.FullName
}

function Sign-ReleaseFile {
    param(
        [Parameter(Mandatory)][string]$SignTool,
        [Parameter(Mandatory)][string]$CertificateThumbprint,
        [Parameter(Mandatory)][string]$Path,
        [switch]$LocalTest
    )

    $signArguments = @("sign", "/sha1", $CertificateThumbprint, "/fd", "SHA256")
    if (-not $LocalTest) {
        $timestampUrl = if ([string]::IsNullOrWhiteSpace($env:CSKIN_TIMESTAMP_URL)) {
            "http://timestamp.digicert.com"
        } else {
            $env:CSKIN_TIMESTAMP_URL
        }
        $signArguments += @("/tr", $timestampUrl, "/td", "SHA256")
    }
    $signArguments += $Path
    & $SignTool @signArguments
    if ($LASTEXITCODE -ne 0) { throw "代码签名失败：$Path" }

    if ($LocalTest) {
        # A self-signed test chain is expected to report UnknownError. Check
        # the embedded signer instead of pretending that it is trusted.
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        if ($null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "本地签名验证失败：$Path"
        }
        return
    }

    & $SignTool verify /pa /all /v $Path
    if ($LASTEXITCODE -ne 0) { throw "签名验证失败：$Path" }
}

$signTool = $null
$certificateThumbprint = $env:CSKIN_CODESIGN_CERT_SHA1
if ($LocalTestBuild) {
    if ([string]::IsNullOrWhiteSpace($certificateThumbprint)) {
        $localCertificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object {
                $_.Subject -eq "CN=Cskin Native Local Test Code Signing" -and
                $_.HasPrivateKey -and
                $_.NotAfter -gt (Get-Date)
            } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($null -eq $localCertificate) {
            throw "未找到本地测试证书。请先运行 installer\setup-local-signing.ps1。"
        }
        $certificateThumbprint = $localCertificate.Thumbprint
    }
    $localSigningCertificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq ($certificateThumbprint -replace '\s', '') } |
        Select-Object -First 1
    if ($null -eq $localSigningCertificate -or -not $localSigningCertificate.HasPrivateKey) {
        throw "找不到带私钥的本地代码签名证书：$certificateThumbprint"
    }
    if (-not ($localSigningCertificate.EnhancedKeyUsageList | Where-Object { ([string]$_.ObjectId) -eq '1.3.6.1.5.5.7.3.3' })) {
        throw "本地证书不包含 Code Signing 用途：$certificateThumbprint"
    }
    $signTool = Get-SignTool
} elseif (-not $UnsignedTestBuild) {
    if ([string]::IsNullOrWhiteSpace($certificateThumbprint)) {
        throw "缺少 CSKIN_CODESIGN_CERT_SHA1。正式发布必须使用受信任的 Authenticode 证书；本地验证请显式使用 -UnsignedTestBuild。"
    }
    $releaseCertificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq ($certificateThumbprint -replace '\s', '') } |
        Select-Object -First 1
    if ($null -eq $releaseCertificate -or -not $releaseCertificate.HasPrivateKey) {
        throw "找不到带私钥的代码签名证书：$certificateThumbprint"
    }
    if (-not ($releaseCertificate.EnhancedKeyUsageList | Where-Object { ([string]$_.ObjectId) -eq '1.3.6.1.5.5.7.3.3' })) {
        throw "证书不包含 Code Signing 用途：$certificateThumbprint"
    }
    if ($releaseCertificate.Issuer -eq $releaseCertificate.Subject) {
        throw "正式发布不能使用自签名证书：$certificateThumbprint"
    }
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    if (-not $chain.Build($releaseCertificate)) {
        $chainStatus = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; '
        $chain.Dispose()
        throw "正式发布证书链不受信任：$certificateThumbprint。$chainStatus"
    }
    $chain.Dispose()
    $signTool = Get-SignTool
}

dotnet publish (Join-Path $projectRoot "CskinNative.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:StripSymbols=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

# MSBuild can reuse a newer signed copy from bin/ between builds when content
# items use PreserveNewest. Sync the engine from the authoritative Runtime tree
# explicitly so an installer can never carry stale PyInstaller output.
$sourceEngineRoot = Join-Path $projectRoot "Runtime\Cskin"
$targetEngineRoot = Join-Path $publishDir "Engine"
if (-not (Test-Path -LiteralPath $sourceEngineRoot)) {
    throw "引擎源目录不存在：$sourceEngineRoot"
}
Get-ChildItem -LiteralPath $sourceEngineRoot -Recurse -File | ForEach-Object {
    # Windows PowerShell 5.1 does not expose Path.GetRelativePath.
    $relative = $_.FullName.Substring($sourceEngineRoot.Length) -replace '^[\\/]+', ''
    $segments = $relative.Split([char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar))
    if ($segments.Count -gt 0 -and $segments[0] -in @("data", "skins", "selector_assets", "__pycache__")) {
        return
    }
    if ($relative.Equals("cskin_engine.py", [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    $target = Join-Path $targetEngineRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}

# Keep the engine directly runnable after the whole folder is copied. The
# native app also refreshes these files at startup, but a bundled selector
# should not depend on that first-run side effect.
$engineAssetsRoot = Join-Path $targetEngineRoot "selector_assets"
New-Item -ItemType Directory -Force -Path $engineAssetsRoot | Out-Null
foreach ($assetName in @("skin_index.json", "skin_names_zh_CN.json", "champions_zh_CN.json")) {
    $assetSource = Join-Path $publishDir ("Assets\" + $assetName)
    if (-not (Test-Path -LiteralPath $assetSource)) {
        throw "发布资源缺失：$assetSource"
    }
    Copy-Item -LiteralPath $assetSource -Destination (Join-Path $engineAssetsRoot $assetName) -Force
}

if (-not (Test-Path (Join-Path $publishDir "PortableCskin.exe"))) {
    throw "发布文件不存在：$publishDir\PortableCskin.exe"
}

$requiredEngineFiles = @(
    (Join-Path $publishDir "Engine\Cskin.exe"),
    (Join-Path $publishDir "Engine\cslol-dll.dll"),
    (Join-Path $publishDir "Engine\tools\cslol-dll.dll"),
    (Join-Path $publishDir "Engine\tools\mod-tools.exe")
)
foreach ($path in $requiredEngineFiles) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "发布文件缺失：$path。请使用包含完整 one-dir 引擎的 Runtime\Cskin 重新构建。"
    }
}

if (-not $UnsignedTestBuild) {
    # Keep the upstream cslol injection DLLs byte-for-byte intact. They carry
    # the vendor signature and are loaded inside the game process; replacing
    # that certificate with a local test signature can trigger anti-cheat or a
    # client access violation. Only sign binaries owned by this application.
    $signableFiles = @(
        (Join-Path $publishDir "PortableCskin.exe"),
        (Join-Path $publishDir "PortableCskin.dll"),
        (Join-Path $publishDir "Engine\Cskin.exe"),
        (Join-Path $publishDir "Engine\tools\mod-tools.exe")
    )
    foreach ($path in $signableFiles) {
        if (Test-Path -LiteralPath $path) {
            Sign-ReleaseFile -SignTool $signTool -CertificateThumbprint $certificateThumbprint -Path $path -LocalTest:$LocalTestBuild
        }
    }
}

New-Item -ItemType Directory -Force $outputDir | Out-Null
$isccArguments = @()
if ($UnsignedTestBuild) { $isccArguments += "/DUnsignedTestBuild=1" }
if ($LocalTestBuild) { $isccArguments += "/DLocalTestBuild=1" }
$isccArguments += (Join-Path $PSScriptRoot "CskinSetup.iss")
& $iscc @isccArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出码：$LASTEXITCODE"
}

$installerFileName = if ($UnsignedTestBuild) {
    "CskinSetup-UNSIGNED-TEST.exe"
} elseif ($LocalTestBuild) {
    "CskinSetup-LOCAL-SIGNED-TEST.exe"
} else {
    "CskinSetup.exe"
}
$installerPath = Join-Path $outputDir $installerFileName
if (-not $UnsignedTestBuild) {
    Sign-ReleaseFile -SignTool $signTool -CertificateThumbprint $certificateThumbprint -Path $installerPath -LocalTest:$LocalTestBuild
}

Get-Item $installerPath
