param(
    [Parameter(Mandatory = $true)]
    [string]$TestPackageDirectory
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as administrator before running this script.'
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testRoot = (Resolve-Path -LiteralPath $TestPackageDirectory).Path
$packagePath = Join-Path $testRoot 'Lilium-Test-x64.msix'
$certificatePath = Join-Path $testRoot 'Lilium-Test.cer'
$dependencyPath = Join-Path $testRoot 'Microsoft.WindowsAppRuntime.2.msix'
$appcert = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
foreach ($required in @($packagePath, $certificatePath, $dependencyPath, $appcert)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file not found: $required" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $archive.Dispose() }
$identity = $manifest.Package.Identity
if ($identity.Name -ne 'Liliales.Lilium' -or
    $identity.Publisher -ne 'CN=41F1C16E-24D1-45BD-9222-F8AD2E499279') {
    throw 'This is not the expected Lilium Store identity.'
}
if (Get-AppxPackage -Name $identity.Name) {
    throw 'Lilium is already registered for this Windows account. Use a dedicated test account to preserve existing app data.'
}

$output = Join-Path $projectRoot ('AppPackages\Certification-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output | Out-Null
$reportPath = Join-Path $output 'WackReport.xml'
$statusPath = Join-Path $output 'status.json'
$status = [ordered]@{ State = 'Starting'; Version = [string]$identity.Version; Report = $reportPath }
$status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$certificateStorePath = 'Cert:\LocalMachine\TrustedPeople\' + $certificate.Thumbprint
$certificateAdded = $false
$installedPackage = $null
try {
    if ($certificate.Subject -ne $identity.Publisher) { throw 'The certificate publisher does not match the package.' }
    if (-not (Test-Path -LiteralPath $certificateStorePath)) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        $certificateAdded = $true
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $packagePath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The signed test package could not be verified.'
    }
    Add-AppxPackage -Path $packagePath -DependencyPath $dependencyPath
    $installedPackage = Get-AppxPackage -Name $identity.Name
    if (-not $installedPackage) { throw 'The installed test package was not found.' }
    $status.State = 'Installed'
    $status.PackageFullName = $installedPackage.PackageFullName
    $status.InstallLocation = $installedPackage.InstallLocation
    $status.SignatureKind = [string]$installedPackage.SignatureKind
    $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8

    $reset = Start-Process -FilePath $appcert -ArgumentList 'reset' -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $output 'reset-stdout.txt') -RedirectStandardError (Join-Path $output 'reset-stderr.txt')
    if ($reset.ExitCode -ne 0) { throw "WACK reset failed: $($reset.ExitCode)" }
    $status.State = 'Testing'
    $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    $test = Start-Process -FilePath $appcert -ArgumentList @('test', '-packagefullname', $installedPackage.PackageFullName,
        '-reportoutputpath', ('"' + $reportPath + '"')) -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $output 'wack-stdout.txt') -RedirectStandardError (Join-Path $output 'wack-stderr.txt')
    $status.ExitCode = $test.ExitCode
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'WACK did not create a report. Check the captured logs.' }
    [xml]$report = Get-Content -LiteralPath $reportPath -Raw
    $status.OverallResult = [string]$report.REPORT.OVERALL_RESULT
    $status.TestResults = @($report.SelectNodes('//REQUIREMENT/TEST') | ForEach-Object {
        [ordered]@{
            Index = [string]$_.INDEX
            Name = [string]$_.NAME
            Optional = [string]$_.OPTIONAL
            Result = [string]$_.RESULT.InnerText
        }
    })
    $status.State = 'ReportReady'
    Write-Output "WACK report: $reportPath"
    Write-Output "WACK overall result: $($status.OverallResult). Review the report; a generated report alone does not mean PASS."
}
catch {
    $status.State = 'Failed'
    $status.Error = $_.Exception.Message
    throw
}
finally {
    try {
        try {
            if ($installedPackage) { Remove-AppxPackage -Package $installedPackage.PackageFullName }
        }
        finally {
            if ($certificateAdded) { Remove-Item -LiteralPath $certificateStorePath }
        }
    }
    finally {
        $certificate.Dispose()
        $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    }
}
