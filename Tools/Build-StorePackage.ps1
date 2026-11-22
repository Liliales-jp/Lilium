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

$output = Join-Path $projectRoot ('AppPackages\Store-' + $identity.Version + '-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output | Out-Null

Push-Location $projectRoot
try {
    dotnet restore Lilium.csproj --configfile NuGet.Config -p:Configuration=Release -p:Platform=x64 -p:LiliumStorePackage=true
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

    # Use the .NET SDK's MSBuild, rather than an older Visual Studio MSBuild.
    # The SDK MSIX tasks can build the package, but their Store container/symbol
    # tasks require extra VS components. Create the documented ZIP container below.
    dotnet msbuild Lilium.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:LiliumStorePackage=true /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /p:AppxSymbolPackageEnabled=false /p:AppxBundle=Never "/p:AppxPackageDir=$output\" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed.' }

    $packages = @(Get-ChildItem -LiteralPath $output -Recurse -Filter 'Lilium_*.msix' -File)
    if ($packages.Count -ne 1) { throw 'Expected exactly one newly built Lilium MSIX.' }
    $msix = $packages[0]
    $archive = [System.IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        if ('coreclr.dll' -notin $entries -or 'hostpolicy.dll' -notin $entries) {
            throw 'The MSIX does not contain the .NET self-contained runtime.'
        }
        $unwanted = @($entries | Where-Object {
            $_ -match '(^|[\\/])(Tests|Tools|_external|\.git|\.nuget-packages)([\\/]|$)' -or
            $_ -match '\.(cs|csproj|ps1|pdb|pfx|cer|db|sqlite|log)$'
        })
        if ($unwanted.Count) { throw "Development files found in MSIX: $($unwanted -join ', ')" }
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { $packageManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($packageManifest -notmatch 'PackageDependency Name="Microsoft.WindowsAppRuntime\.2"') {
            throw 'The Windows App SDK framework package dependency is missing.'
        }
        [xml]$builtManifest = $packageManifest
        foreach ($attribute in @('Name', 'Publisher', 'Version')) {
            if ($builtManifest.Package.Identity.$attribute -ne $identity.$attribute) {
                throw "The built MSIX has an unexpected Identity $attribute."
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    # https://learn.microsoft.com/windows/msix/package/packaging-uwp-apps
    # Public symbols are optional. Keep development symbols out of the app.
    $uploadPath = Join-Path $output ([System.IO.Path]::GetFileNameWithoutExtension($msix.Name) + '.msixupload')
    $upload = [System.IO.Compression.ZipFile]::Open($uploadPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $upload, $msix.FullName, $msix.Name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    finally { $upload.Dispose() }
    Write-Output $uploadPath
}
finally {
    Pop-Location
}
