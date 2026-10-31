$ErrorActionPreference = "Stop"
$ssmsDir = "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE"
$projDir = Join-Path $PSScriptRoot "ObjectFinder.Vsix"
$proj    = Join-Path $projDir "ObjectFinder.Vsix.csproj"

if (Get-Process ssms -ErrorAction SilentlyContinue) { throw "Close SSMS first." }

msbuild $proj -p:Configuration=Debug -v:m
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$vsix = Get-ChildItem $projDir -Recurse -Filter *.vsix |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
[xml]$manifest = Get-Content (Join-Path $projDir "source.extension.vsixmanifest")
$id = $manifest.PackageManifest.Metadata.Identity.Id

# Uninstall first: the installer may refuse to reinstall the same version
Start-Process "$ssmsDir\VSIXInstaller.exe" -ArgumentList "/quiet", "/uninstall:$id" -Wait
Start-Process "$ssmsDir\VSIXInstaller.exe" -ArgumentList "/quiet", "`"$($vsix.FullName)`"" -Wait

Start-Process "$ssmsDir\Ssms.exe" -ArgumentList "-log", "C:\temp\ssms.xml"