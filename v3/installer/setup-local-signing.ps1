[CmdletBinding()]
param(
    # Recreate the certificate when a previous local test certificate expired
    # or was removed from the current-user certificate store.
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$subject = "CN=Cskin Native Local Test Code Signing"
$outputDir = Join-Path $env:LOCALAPPDATA "CskinNative\signing"
$certificatePath = Join-Path $outputDir "CskinNative-LocalTestCodeSigning.cer"

$certificate = if (-not $Force) {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $subject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            ($_.EnhancedKeyUsageList | Where-Object { ([string]$_.ObjectId) -eq "1.3.6.1.5.5.7.3.3" })
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if ($null -eq $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -HashAlgorithm SHA256 `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -NotBefore (Get-Date).AddMinutes(-5) `
        -NotAfter (Get-Date).AddYears(3)
}

New-Item -ItemType Directory -Force $outputDir | Out-Null
Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null

$trustedPublisher = Get-ChildItem Cert:\CurrentUser\TrustedPublisher |
    Where-Object Thumbprint -eq $certificate.Thumbprint
if ($null -eq $trustedPublisher) {
    Import-Certificate -FilePath $certificatePath `
        -CertStoreLocation Cert:\CurrentUser\TrustedPublisher `
        -Confirm:$false | Out-Null
}

[Environment]::SetEnvironmentVariable("CSKIN_CODESIGN_CERT_SHA1", $certificate.Thumbprint, "User")
$env:CSKIN_CODESIGN_CERT_SHA1 = $certificate.Thumbprint

Write-Host "本地测试代码签名证书已配置。"
Write-Host "主题：$($certificate.Subject)"
Write-Host "指纹：$($certificate.Thumbprint)"
Write-Host "证书文件：$certificatePath"
Write-Warning "该证书是自签名证书，仅用于本机测试；不能让其他用户电脑显示受信任发布者，也不能用于正式发布。"
