[CmdletBinding()]
param(
    [string] $PythonPath,
    [string] $VulkanSdkPath = $env:VULKAN_SDK,
    [string] $BuildDirectory = (Join-Path $PSScriptRoot 'build'),
    [string] $CaptureOneDirectory = (Join-Path $env:ProgramFiles 'Capture One\Capture One'),
    [int] $Parallel = 2,
    [switch] $BuildPanel,
    [switch] $StageRuntime,
    [switch] $SkipSmoke
)

$ErrorActionPreference = 'Stop'
if ($Parallel -lt 1) { throw 'Parallel must be at least one.' }

function Invoke-Checked([string] $Program, [string[]] $Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with code $LASTEXITCODE." }
}

if (!$PythonPath) {
    $PythonPath = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
    if (!(Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
        $launcher = Get-Command py -CommandType Application -ErrorAction SilentlyContinue
        if ($launcher) {
            $PythonPath = & $launcher.Source -3.13 -c 'import sys; print(sys.executable)'
        } else {
            $PythonPath = (Get-Command python -CommandType Application -ErrorAction SilentlyContinue).Source
        }
        if (!$PythonPath) { throw 'Install 64-bit Python 3.13 or pass its executable with -PythonPath.' }
    }
}
$PythonPath = (Get-Command $PythonPath -CommandType Application).Source
& $PythonPath -c 'import struct, sys; sys.exit(sys.version_info[:2] != (3, 13) or struct.calcsize("P") != 8)'
if ($LASTEXITCODE -ne 0) { throw 'Choose a 64-bit Python 3.13 executable.' }
& $PythonPath -c 'import numpy, scipy, colour'
if ($LASTEXITCODE -ne 0) {
    $requirements = Join-Path $PSScriptRoot 'requirements-build.txt'
    throw "Run & '$PythonPath' -m pip install -r '$requirements', then retry."
}

$cmake = (Get-Command cmake -CommandType Application).Source
if ($VulkanSdkPath) {
    $VulkanSdkPath = (Resolve-Path -LiteralPath $VulkanSdkPath).ProviderPath
}
$BuildDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BuildDirectory)
$harness = Join-Path $BuildDirectory 'ofx-harness'
$helper = Join-Path $BuildDirectory 'still-renderer'
$generator = @('-G', 'Visual Studio 17 2022', '-A', 'x64')
$previousVulkanSdk = $env:VULKAN_SDK
try {
    if ($VulkanSdkPath) { $env:VULKAN_SDK = $VulkanSdkPath }
    Invoke-Checked $cmake (@('-S', (Join-Path $PSScriptRoot 'upstream-ofx'), '-B', $harness) + $generator +
        @('-DSPEKTRAFILM_RELEASE_LTO=OFF', "-DPython3_EXECUTABLE=$PythonPath"))
    Invoke-Checked $cmake @('--build', $harness, '--config', 'Release', '--target', 'SpektraVulkanCopyHarness', '--parallel', "$Parallel")
    Invoke-Checked $cmake (@('-S', (Join-Path $PSScriptRoot 'ofx-still-renderer'), '-B', $helper) + $generator + @("-DOFX_BUILD=$harness"))
    Invoke-Checked $cmake @('--build', $helper, '--config', 'Release', '--target', 'SpektraStillRenderer', '--parallel', "$Parallel")
} finally {
    $env:VULKAN_SDK = $previousVulkanSdk
}

$output = Join-Path $helper 'Release'
$executable = Join-Path $output 'SpektraStillRenderer.exe'
if (!$SkipSmoke) {
    Invoke-Checked $PythonPath @((Join-Path $PSScriptRoot 'ofx-still-renderer\smoke.py'), '--exe', $executable)
}

$files = @('SpektraStillRenderer.exe', 'SpektraHanatos2025Spectra.f32', 'SpektraOutputGamutCompression.f32')
$shaders = 'Copy', 'FormatConvert', 'FilmExposure', 'CurveDevelop', 'PrintScan', 'Halation', 'Diffusion', 'Dir', 'ScannerPost', 'Grain'
$files += $shaders | ForEach-Object { "shaders\Spektra$_.comp.spv" }
$runtime = Join-Path $PSScriptRoot 'renderer'
foreach ($file in $files) {
    $built = Get-Item -LiteralPath (Join-Path $output $file)
    if ($built.PSIsContainer -or $built.Length -eq 0) { throw "Missing runtime file $file." }
    if ($StageRuntime) {
        $destination = Join-Path $runtime $file
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Copy-Item -LiteralPath $built.FullName -Destination $destination -Force
        Write-Output $destination
    } else {
        Write-Output $built.FullName
    }
}

if ($BuildPanel) {
    $CaptureOneDirectory = (Resolve-Path -LiteralPath $CaptureOneDirectory).ProviderPath
    if (!(Test-Path -LiteralPath (Join-Path $CaptureOneDirectory 'CaptureOne.dll') -PathType Leaf)) {
        throw 'Pass the directory of the installed Capture One application with -CaptureOneDirectory.'
    }
    $dotnet = (Get-Command dotnet -CommandType Application).Source
    $panelBuild = Join-Path $BuildDirectory 'panel'
    Invoke-Checked $dotnet @('build', (Join-Path $PSScriptRoot 'NativePanel\NativePanel.csproj'), '-c', 'Release',
        '-p:TargetPlatformDisplayName=Windows', "-p:CaptureOneDirectory=$CaptureOneDirectory", '--output', $panelBuild)
    $hook = Join-Path $panelBuild 'NativePanel.dll'
    if ($StageRuntime) {
        $panel = Join-Path $PSScriptRoot 'panel'
        [IO.Directory]::CreateDirectory($panel) | Out-Null
        Copy-Item -LiteralPath $hook -Destination $panel -Force
        Write-Output (Join-Path $panel 'NativePanel.dll')
    } else {
        Write-Output $hook
    }
}
