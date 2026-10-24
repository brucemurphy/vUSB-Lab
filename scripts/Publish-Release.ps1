#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'vUSB LAB.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
if ([string]$xml.Project.PropertyGroup[0].Version -ne $Version) {
    throw 'The requested version must match the project Version property.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with MSBuild is required.' }
$msbuild = & $vswhere -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found.' }
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required to build the EXE installer (winget install JRSoftware.InnoSetup).' }
$artifacts = Join-Path $root 'artifacts'
$null = New-Item -ItemType Directory -Path $artifacts -Force
$checksums = @()
foreach ($architecture in 'x64', 'arm64') {
    $platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    $folder = Join-Path $artifacts "publish-win-$architecture"
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    & $msbuild $project -restore -target:Publish -nologo -verbosity:minimal `
        "-p:Configuration=Release" "-p:Platform=$platform" "-p:RuntimeIdentifier=win-$architecture" `
        "-p:PublishDir=$folder" '-p:SelfContained=true' '-p:WindowsAppSDKSelfContained=true' `
        '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:PublishReadyToRun=false'
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $architecture." }
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'vUSB LAB.exe'))) { throw 'Published executable is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'Hyper-V VHDX Toggle Manager\vhdx-toggle-flash-drive.png'))) {
        throw 'Published USB artwork is missing.'
    }
    $zip = Join-Path $artifacts "vUSB-Lab-v$Version-win-$architecture.zip"
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $zip -Force
    $checksums += "$((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))"
    & $iscc /Q "/DAppVersion=$Version" "/DArchitecture=$architecture" "/DSourceDir=$folder" "/O$artifacts" (Join-Path $PSScriptRoot 'Installer.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed for $architecture." }
    $installer = Join-Path $artifacts "vUSB-Lab-v$Version-win-$architecture-Setup.exe"
    if (-not (Test-Path -LiteralPath $installer)) { throw 'Compiled installer is missing.' }
    $checksums += "$((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($installer))"
}
$checksums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Release v$Version is ready in $artifacts"
