param(
    [string]$SsmsDir,
    [string]$SsmsLogPath = (Join-Path $env:TEMP "ssms.xml")
)

$ErrorActionPreference = "Stop"
$projDir = Join-Path $PSScriptRoot "ObjectFinder.Vsix"
$proj    = Join-Path $projDir "ObjectFinder.Vsix.csproj"

function Resolve-SsmsDir
{
    param([string]$PreferredDir)

    $candidateDirs = New-Object System.Collections.Generic.List[string]

    if ($PreferredDir)
    {
        $candidateDirs.Add($PreferredDir)
    }

    $ssmsCommand = Get-Command "Ssms.exe" -ErrorAction SilentlyContinue
    if ($ssmsCommand)
    {
        $candidateDirs.Add((Split-Path $ssmsCommand.Source -Parent))
    }

    foreach ($appPathKey in @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Ssms.exe",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Ssms.exe"))
    {
        $appPath = Get-ItemProperty -Path $appPathKey -ErrorAction SilentlyContinue
        if ($appPath)
        {
            if ($appPath.Path)
            {
                $candidateDirs.Add($appPath.Path)
            }

            if ($appPath.'(default)')
            {
                $candidateDirs.Add((Split-Path $appPath.'(default)' -Parent))
            }
        }
    }

    foreach ($baseDir in @($env:ProgramFiles, ${env:ProgramFiles(x86)}))
    {
        if (-not $baseDir) { continue }

        Get-ChildItem -Path $baseDir -Directory -Filter "Microsoft SQL Server Management Studio*" -ErrorAction SilentlyContinue |
            ForEach-Object {
                $candidateDirs.Add((Join-Path $_.FullName "Common7\IDE"))
                $candidateDirs.Add((Join-Path $_.FullName "Release\Common7\IDE"))
            }
    }

    foreach ($uninstallKey in @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"))
    {
        Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "SQL Server Management Studio*" -and $_.InstallLocation } |
            ForEach-Object {
                $candidateDirs.Add($_.InstallLocation)
                $candidateDirs.Add((Join-Path $_.InstallLocation "Common7\IDE"))
                $candidateDirs.Add((Join-Path $_.InstallLocation "Release\Common7\IDE"))
            }
    }

    foreach ($dir in $candidateDirs | Where-Object { $_ } | Select-Object -Unique)
    {
        $vsixInstallerPath = Join-Path $dir "VSIXInstaller.exe"
        $ssmsExePath = Join-Path $dir "Ssms.exe"
        if ((Test-Path $vsixInstallerPath) -and (Test-Path $ssmsExePath))
        {
            return $dir
        }
    }

    return $null
}

$SsmsDir = Resolve-SsmsDir -PreferredDir $SsmsDir

$vsixInstallerPath = if ($SsmsDir) { Join-Path $SsmsDir "VSIXInstaller.exe" } else { $null }
$ssmsExePath = if ($SsmsDir) { Join-Path $SsmsDir "Ssms.exe" } else { $null }

if (-not $vsixInstallerPath -or -not (Test-Path $vsixInstallerPath) -or -not (Test-Path $ssmsExePath))
{
    throw "Could not locate SSMS tools. Pass -SsmsDir <SSMS IDE folder> (for example: ...\Microsoft SQL Server Management Studio 22\Release\Common7\IDE)."
}

if (Get-Process ssms -ErrorAction SilentlyContinue) { throw "Close SSMS first." }

msbuild $proj -p:Configuration=Debug -v:m
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$vsix = Get-ChildItem $projDir -Recurse -Filter *.vsix |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
[xml]$manifest = Get-Content (Join-Path $projDir "source.extension.vsixmanifest")
$id = $manifest.PackageManifest.Metadata.Identity.Id

$logDir = Split-Path $SsmsLogPath -Parent
if ($logDir -and -not (Test-Path $logDir))
{
    New-Item -Path $logDir -ItemType Directory -Force | Out-Null
}

# Uninstall first: the installer may refuse to reinstall the same version
Start-Process $vsixInstallerPath -ArgumentList "/quiet", "/uninstall:$id" -Wait
Start-Process $vsixInstallerPath -ArgumentList "/quiet", "`"$($vsix.FullName)`"" -Wait

Start-Process $ssmsExePath -ArgumentList "-log", $SsmsLogPath