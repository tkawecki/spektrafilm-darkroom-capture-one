[CmdletBinding()]
param([string] $PythonPath)

$ErrorActionPreference = 'Stop'
$requirements = Join-Path $PSScriptRoot 'requirements-lut.txt'
$worker = Join-Path $PSScriptRoot 'lut_worker.py'
$environment = Join-Path $PSScriptRoot '.venv'
$python = Join-Path $environment 'Scripts\python.exe'
$versionCheck = 'import sys; sys.exit(not (sys.version_info[:2] == (3, 13) and sys.maxsize > 2**32))'

function Invoke-Python([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Python failed with exit code $LASTEXITCODE." }
}

try {
    foreach ($file in @($requirements, $worker, (Join-Path $PSScriptRoot 'upstream-spektrafilm\src\spektrafilm\__init__.py'))) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing optional LUT source file: $file" }
    }
    if (!(Test-Path -LiteralPath $python -PathType Leaf)) {
        $candidate = $PythonPath
        $launcherArgs = @()
        if (!$candidate) {
            $localPython = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Python\Python313\python.exe'
            if (Test-Path -LiteralPath $localPython -PathType Leaf) { $candidate = $localPython }
        }
        if (!$candidate) {
            $launcher = Get-Command 'py.exe' -ErrorAction SilentlyContinue
            if ($launcher) {
                $candidate = $launcher.Source
                $launcherArgs = @('-3.13')
            } else {
                $pythonCommand = Get-Command 'python.exe' -ErrorAction SilentlyContinue
                if ($pythonCommand) { $candidate = $pythonCommand.Source }
            }
        }
        if (!$candidate) { throw 'Install 64-bit Python 3.13 from python.org, or pass -PythonPath to this script.' }
        & $candidate @launcherArgs -c $versionCheck
        if ($LASTEXITCODE -ne 0) { throw 'Use 64-bit Python 3.13. Pass -PythonPath if it is installed in a custom location.' }
        Invoke-Python $candidate ($launcherArgs + @('-m', 'venv', $environment))
    }
    Invoke-Python $python @('-c', $versionCheck)
    Invoke-Python $python @('-m', 'pip', 'install', '--only-binary=:all:', '-r', $requirements)
    $request = '{"command":"export_lut","size":33,"strength":0}'
    $reply = $request | & $python $worker
    if ($LASTEXITCODE -ne 0) { throw 'The LUT worker failed its import check.' }
    $response = $reply | ConvertFrom-Json
    if (!$response.ok -or $response.cube -notmatch '(?m)^LUT_3D_SIZE 33$') {
        throw "The LUT worker did not return a valid test LUT. $($response.error)"
    }
    Write-Host 'LUT export is ready. Restart Capture One from the Spektrafilm Darkroom for Capture One desktop shortcut.'
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
