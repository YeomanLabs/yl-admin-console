<#
.SYNOPSIS
    Builds the console's release files: an MSI and a portable zip per platform, plus SHA-256 sums.

.DESCRIPTION
    For each platform: a self-contained Release publish (.NET 8 and WPF included, no debug symbols), checked for
    anything local that must never ship, zipped as is, then packed into a per-machine MSI with WiX
    (installer\AdminConsole.Installer.wixproj). Output goes to artifacts\<version>\.

    The shipped appsettings.json loses its client ID (DECISIONS, M10): a new installation opens the setup screen and is
    pointed at its own tenant's registration. The build stops if the publish folder holds appsettings.local.json or any
    JSON with a password in it — that file carries a lab credential on a development machine.

    The version comes from AdminConsole.App.csproj unless -Version is given.

.EXAMPLE
    pwsh scripts\Build-Release.ps1
.EXAMPLE
    pwsh scripts\Build-Release.ps1 -Platforms x64
#>
param(
    [string]$Version,
    [ValidateSet('x64', 'ARM64')]
    [string[]]$Platforms = @('x64', 'ARM64'),
    [string]$ProductName = 'Admin Console',
    [string]$Manufacturer = 'Admin Console'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$app = Join-Path $repo 'src\AdminConsole.App\AdminConsole.App.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $app -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$out = Join-Path $repo "artifacts\$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($platform in $Platforms) {
    $rid = if ($platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $arch = $platform.ToLowerInvariant()
    $publish = Join-Path $repo "artifacts\publish\$arch"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    "== ${platform}: publish $Version"
    dotnet publish $app -c Release -r $rid --self-contained -p:Version=$Version -p:InformationalVersion=$Version `
        -p:DebugType=None -p:DebugSymbols=false -o $publish -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $platform." }
    if (-not (Test-Path (Join-Path $publish 'AdminConsole.exe'))) { throw "No AdminConsole.exe in $publish." }

    "== ${platform}: check nothing local ships"
    if (Test-Path (Join-Path $publish 'appsettings.local.json')) { throw "appsettings.local.json is in $publish. It must never ship." }
    $settingsPath = Join-Path $publish 'appsettings.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings.Auth.ClientId = ''
    $settings | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding utf8
    foreach ($json in Get-ChildItem $publish -Filter *.json -Recurse) {
        if ((Get-Content $json.FullName -Raw) -match '"(password|credential)"\s*:') { throw "$($json.Name) carries a credential. Nothing is packaged." }
    }

    "== ${platform}: zip"
    $zip = Join-Path $out "AdminConsole-$Version-$arch.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal

    "== ${platform}: msi"
    dotnet build (Join-Path $repo 'installer\AdminConsole.Installer.wixproj') -c Release -p:Platform=$platform `
        -p:Version=$Version -p:ProductName="$ProductName" -p:Manufacturer="$Manufacturer" -p:PublishDir="$publish\" `
        -o (Join-Path $repo "artifacts\installer\$arch") -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed for $platform." }
    Copy-Item (Join-Path $repo "artifacts\installer\$arch\AdminConsole.msi") (Join-Path $out "AdminConsole-$Version-$arch.msi")
}

$sums = Get-ChildItem $out -File | Where-Object { $_.Extension -in '.msi', '.zip' } | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content -Path (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding ascii

Get-ChildItem $out | Select-Object Name, @{ Name = 'MB'; Expression = { [Math]::Round($_.Length / 1MB, 1) } }
