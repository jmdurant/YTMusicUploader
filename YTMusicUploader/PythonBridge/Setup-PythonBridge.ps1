<#
.SYNOPSIS
    Provisions a self-contained Python runtime for the YTMusicUploader
    Python bridge in .\Python next to this script.

.DESCRIPTION
    Downloads the official python.org Windows embeddable package (pinned
    3.12.x, amd64), extracts it to .\Python, enables site-packages in the
    ._pth file, bootstraps pip via get-pip.py, and installs the packages
    listed in requirements.txt.

    Idempotent: if .\Python\python.exe already exists and can import
    ytmusicapi, the script exits immediately. Re-run with -Force to
    re-provision from scratch.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # dramatically speeds up Invoke-WebRequest

$PythonVersion = '3.12.10'
$EmbedZipUrl   = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
$GetPipUrl     = 'https://bootstrap.pypa.io/get-pip.py'

$Root         = $PSScriptRoot
$PythonDir    = Join-Path $Root 'Python'
$PythonExe    = Join-Path $PythonDir 'python.exe'
$Requirements = Join-Path $Root 'requirements.txt'

function Test-BridgeRuntime {
    if (-not (Test-Path $PythonExe)) { return $false }
    & $PythonExe -c 'import ytmusicapi' 2>$null
    return ($LASTEXITCODE -eq 0)
}

if (-not $Force -and (Test-BridgeRuntime)) {
    Write-Host "Python bridge runtime already provisioned at $PythonDir (ytmusicapi importable). Nothing to do."
    exit 0
}

if ($Force -and (Test-Path $PythonDir)) {
    Write-Host "Removing existing runtime at $PythonDir ..."
    Remove-Item -Recurse -Force $PythonDir
}

if (-not (Test-Path $Requirements)) {
    throw "requirements.txt not found at $Requirements"
}

# --- 1. Download and extract the embeddable distribution --------------------
if (-not (Test-Path $PythonExe)) {
    $zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "python-$PythonVersion-embed-amd64.zip"
    Write-Host "Downloading $EmbedZipUrl ..."
    Invoke-WebRequest -Uri $EmbedZipUrl -OutFile $zipPath -UseBasicParsing
    Write-Host "Extracting to $PythonDir ..."
    Expand-Archive -Path $zipPath -DestinationPath $PythonDir -Force
    Remove-Item $zipPath -Force
}

# --- 2. Enable site-packages in the ._pth file -------------------------------
# The embeddable distribution ships python3XX._pth with "#import site"
# commented out; pip-installed packages are invisible until it is enabled.
$pthFile = Get-ChildItem -Path $PythonDir -Filter 'python3*._pth' | Select-Object -First 1
if (-not $pthFile) { throw "No python3*._pth file found in $PythonDir" }
$pthContent = Get-Content $pthFile.FullName
if ($pthContent -contains '#import site') {
    Write-Host "Enabling site-packages in $($pthFile.Name) ..."
    ($pthContent -replace '^#import site$', 'import site') | Set-Content $pthFile.FullName -Encoding ascii
}

# --- 3. Bootstrap pip ---------------------------------------------------------
& $PythonExe -m pip --version 2>$null
if ($LASTEXITCODE -ne 0) {
    $getPip = Join-Path ([System.IO.Path]::GetTempPath()) 'get-pip.py'
    Write-Host "Downloading get-pip.py ..."
    Invoke-WebRequest -Uri $GetPipUrl -OutFile $getPip -UseBasicParsing
    Write-Host "Bootstrapping pip ..."
    & $PythonExe $getPip --no-warn-script-location
    if ($LASTEXITCODE -ne 0) { throw "get-pip.py failed with exit code $LASTEXITCODE" }
    Remove-Item $getPip -Force
}

# --- 4. Install requirements --------------------------------------------------
Write-Host "Installing requirements from $Requirements ..."
& $PythonExe -m pip install --no-warn-script-location -r $Requirements
if ($LASTEXITCODE -ne 0) { throw "pip install failed with exit code $LASTEXITCODE" }

# --- 5. Verify ----------------------------------------------------------------
& $PythonExe -c 'import ytmusicapi; print("ytmusicapi", ytmusicapi.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'Verification failed: could not import ytmusicapi' }

Write-Host "Python bridge runtime provisioned successfully at $PythonDir"
