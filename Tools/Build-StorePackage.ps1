param(
    [switch]$AllowPlaceholderIdentity
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $projectRoot 'Package.appxmanifest'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$identity = $manifest.Package.Identity

if (-not $AllowPlaceholderIdentity -and
    ($identity.Name -eq 'Lilium' -or $identity.Publisher -eq 'CN=Lilium')) {
    throw 'Reserve Lilium in Partner Center, then replace Identity Name and Publisher in Package.appxmanifest. Use -AllowPlaceholderIdentity only to test packaging.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio with MSBuild and MSIX packaging tools is required.'
}
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild.exe was not found in Visual Studio.'
}

Push-Location $projectRoot
try {
    dotnet restore Lilium.csproj --configfile NuGet.Config -p:Configuration=Release -p:Platform=x64 -p:LiliumStorePackage=true
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

    & $msbuild Lilium.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:LiliumStorePackage=true /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=StoreUpload /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never '/p:AppxPackageDir=AppPackages\' /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed.' }

    $msix = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'AppPackages') -Recurse -Filter '*.msix' -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msix) { throw 'No MSIX was produced.' }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        if ('coreclr.dll' -notin $entries -or 'hostpolicy.dll' -notin $entries) {
            throw 'The MSIX does not contain the .NET self-contained runtime.'
        }
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { $packageManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($packageManifest -notmatch 'PackageDependency Name="Microsoft.WindowsAppRuntime\.2"') {
            throw 'The Windows App SDK framework package dependency is missing.'
        }
    }
    finally {
        $archive.Dispose()
    }

    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'AppPackages') -Filter '*.msixupload' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
finally {
    Pop-Location
}
