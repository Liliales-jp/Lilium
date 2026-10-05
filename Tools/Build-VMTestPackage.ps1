$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appPackages = Join-Path $projectRoot 'AppPackages'
[xml]$packageManifest = Get-Content -LiteralPath (Join-Path $projectRoot 'Package.appxmanifest') -Raw
$publisher = [string]$packageManifest.Package.Identity.Publisher

& (Join-Path $PSScriptRoot 'Build-StorePackage.ps1') -AllowPlaceholderIdentity
if ($LASTEXITCODE -ne 0) { throw 'The unsigned MSIX build failed.' }

$unsignedMsix = Get-ChildItem -LiteralPath $appPackages -Directory |
    Where-Object Name -Like '*_Test' |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter 'Lilium_*.msix' -File } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $unsignedMsix) { throw 'The unsigned Lilium MSIX was not found.' }

$dependency = Join-Path $unsignedMsix.DirectoryName 'Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix'
if (-not (Test-Path -LiteralPath $dependency)) { throw 'The x64 Windows App SDK dependency was not found.' }

$signtool = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path -LiteralPath $signtool)) { throw 'Windows SDK SignTool.exe was not found.' }

$output = Join-Path $appPackages ('VM-Test-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output | Out-Null
$signedMsix = Join-Path $output 'Lilium-Test-x64.msix'
$publicCert = Join-Path $output 'Lilium-Test.cer'
$tempPfx = Join-Path $output 'signing-temp.pfx'
Copy-Item -LiteralPath $unsignedMsix.FullName -Destination $signedMsix
Copy-Item -LiteralPath $dependency -Destination (Join-Path $output 'Microsoft.WindowsAppRuntime.2.msix')

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$certificate = $null
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $publisher, $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $codeSigning = [System.Security.Cryptography.OidCollection]::new()
    [void]$codeSigning.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($codeSigning, $false))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))

    $now = [DateTimeOffset]::UtcNow
    $certificate = $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddYears(1))
    $randomBytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
    $password = [Convert]::ToHexString($randomBytes)
    [System.IO.File]::WriteAllBytes($tempPfx,
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $password))
    [System.IO.File]::WriteAllBytes($publicCert,
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    if (-not (Test-Path -LiteralPath $tempPfx -PathType Leaf)) {
        throw 'The temporary signing certificate was not written.'
    }
    & $signtool sign /fd SHA256 /f $tempPfx /p $password $signedMsix
    if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the test MSIX.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $signedMsix
    if (-not $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The signed MSIX certificate did not match the exported test certificate.'
    }
}
finally {
    if (Test-Path -LiteralPath $tempPfx) { Remove-Item -LiteralPath $tempPfx -Force }
    if ($certificate) { $certificate.Dispose() }
    $rsa.Dispose()
}

Copy-Item -LiteralPath (Join-Path $projectRoot '_external\vm-test-package.md') -Destination (Join-Path $output 'README.md')
$zipPath = $output + '.zip'
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zipPath
Write-Output $zipPath
