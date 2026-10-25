[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.+-]{0,63}$')]
    [string]$Version = 'local'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts/packages'
$name = "desk-organizer-mcp-$Version-win-x64"
$stage = Join-Path $repo ("artifacts/package-work/" + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage $name
$server = Join-Path $package 'server'
$archive = Join-Path $output "$name.zip"
if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive. Choose another version." }
New-Item -ItemType Directory -Path $server -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

& dotnet publish (Join-Path $repo 'src/DeskOrganizer.Mcp/DeskOrganizer.Mcp.csproj') `
    -c Release -r win-x64 --self-contained true --disable-build-servers -m:1 `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -o $server
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $repo 'skills') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'examples') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'docs/INSTALL.md') -Destination (Join-Path $package 'README.md')
$manifest = [ordered]@{
    name = 'desk-organizer-mcp'; version = $Version; runtime = 'win-x64'
    selfContained = $true; skill = 'skills/desk-organizer/SKILL.md'; server = 'server/DeskOrganizer.Mcp.exe'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'package.json') -Encoding UTF8
Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
