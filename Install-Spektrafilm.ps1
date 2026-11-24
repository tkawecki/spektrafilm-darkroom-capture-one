[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Destination,
    [string] $DesktopDirectory = ([Environment]::GetFolderPath('DesktopDirectory'))
)

$ErrorActionPreference = 'Stop'
$appName = 'Spektrafilm Darkroom for Capture One'
$markerName = '.film-tools-install'
$markerText = 'Spektrafilm Darkroom for Capture One portable installation'
$lockedMessage = "Close $appName to update, then run the installer again."

function Test-OwnedInstall([string] $path) {
    if (!(Test-Path -LiteralPath $path -PathType Container)) { return $false }
    Confirm-PlainItem (Get-Item -LiteralPath $path -Force)
    $marker = Join-Path $path $markerName
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    Confirm-PlainItem (Get-Item -LiteralPath $marker -Force)
    # Only this project's own marker: installations of the original Spektrafilm for Capture One are left alone.
    return [IO.File]::ReadAllText($marker) -ceq $markerText
}

function Test-SamePath([string] $first, [string] $second) {
    return [string]::Equals($first, $second, [StringComparison]::OrdinalIgnoreCase)
}

function Test-BelowPath([string] $child, [string] $parent) {
    return $child.StartsWith($parent.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Confirm-PlainItem($item) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Choose a regular file or folder instead of the linked path $($item.FullName)."
    }
}

function Test-FileInUse([Exception] $error) {
    $cause = $error.GetBaseException()
    return ($cause.HResult -band 0xffff) -in @(32, 33)
}

function Confirm-UpdateFilesAvailable([string] $install) {
    foreach ($relative in @('panel\NativePanel.dll', 'renderer\SpektraStillRenderer.exe')) {
        $path = Join-Path $install $relative
        if (![IO.File]::Exists($path)) { continue }
        try {
            $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            $file.Dispose()
        } catch {
            if (Test-FileInUse $_.Exception) {
                throw $lockedMessage
            }
            throw
        }
    }
}

function Remove-TemporaryInstall([string] $path, [string] $parent) {
    if (![IO.Directory]::Exists($path)) { return }
    $item = Get-Item -LiteralPath $path -Force
    Confirm-PlainItem $item
    if (!(Test-BelowPath $item.FullName $parent) -or !(Test-SamePath $item.Parent.FullName $parent) -or
        $item.Name -notmatch '^\.film-tools-(stage|previous)-[a-f0-9]{32}$') {
        throw "Could not verify temporary installation path $path."
    }
    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}

function Get-ShortcutArguments([string] $launcher) {
    return '-NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "' + $launcher + '"'
}

function Save-DesktopShortcut([string] $path, [string] $target, [string] $launcher, [string] $install, [bool] $upgrade) {
    $baseArguments = Get-ShortcutArguments $launcher
    $arguments = $baseArguments + ' -Shortcut'
    $shell = New-Object -ComObject WScript.Shell
    try {
        if (Test-Path -LiteralPath $path) {
            Confirm-PlainItem (Get-Item -LiteralPath $path -Force)
            $existing = $shell.CreateShortcut($path)
            try {
                if (!(Test-SamePath $existing.TargetPath $target) -or
                    ($existing.Arguments -cne $arguments -and $existing.Arguments -cne $baseArguments)) {
                    throw "A different Desktop shortcut already uses the name $appName. Rename it before running the installer."
                }
            } finally {
                [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($existing)
            }
        }
        $desktop = Split-Path -LiteralPath $path
        $temporaryLink = Join-Path $desktop ('.film-tools-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        $previousLink = Join-Path $desktop ('.film-tools-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        try {
            $shortcut = $shell.CreateShortcut($temporaryLink)
            try {
                $shortcut.TargetPath = $target
                $shortcut.Arguments = $arguments
                $shortcut.WorkingDirectory = $install
                $shortcut.IconLocation = (Join-Path $install 'assets\Spektrafilm.ico') + ',0'
                $shortcut.Description = "Open a Capture One catalog with $appName"
                $shortcut.Save()
            } finally {
                [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
            }
            if ([IO.File]::Exists($path)) { [IO.File]::Replace($temporaryLink, $path, $previousLink) }
            else { [IO.File]::Move($temporaryLink, $path) }
        } finally {
            if ([IO.File]::Exists($temporaryLink)) { [IO.File]::Delete($temporaryLink) }
            if ([IO.File]::Exists($previousLink)) { [IO.File]::Delete($previousLink) }
        }
        if ($upgrade) {
            $legacyArguments = Get-ShortcutArguments (Join-Path $install 'Start-FilmTools.ps1')
            foreach ($name in @('Film Tools for Capture One.lnk', 'Capture One with spektrafilm.lnk')) {
                $legacyPath = Join-Path $desktop $name
                if (!(Test-Path -LiteralPath $legacyPath -PathType Leaf)) { continue }
                try {
                    Confirm-PlainItem (Get-Item -LiteralPath $legacyPath -Force)
                    $legacy = $shell.CreateShortcut($legacyPath)
                    try {
                        $owned = (Test-SamePath $legacy.TargetPath $target) -and
                            ($legacy.Arguments -ceq $legacyArguments -or $legacy.Arguments -ceq ($legacyArguments + ' -Shortcut'))
                    } finally {
                        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($legacy)
                    }
                    if ($owned) { Remove-Item -LiteralPath $legacyPath }
                } catch {
                    Write-Warning "Could not migrate the existing shortcut at $legacyPath."
                }
            }
        }
    } finally {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

try {
    $source = (Get-Item -LiteralPath $PSScriptRoot).FullName.TrimEnd('\', '/')
    if (!$Destination) {
        $programs = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'
        $Destination = Join-Path $programs $appName
    }
    $install = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination).TrimEnd('\', '/')
    $desktop = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DesktopDirectory).TrimEnd('\', '/')
    $parent = Split-Path -LiteralPath $install
    if (!$parent -or (Test-SamePath $install $source) -or (Test-BelowPath $source $install) -or (Test-BelowPath $install $source) -or
        (Test-SamePath $desktop $install) -or (Test-BelowPath $desktop $install)) {
        throw 'Choose an installation folder separate from the extracted ZIP and the Desktop.'
    }
    if (!(Test-Path -LiteralPath $desktop -PathType Container)) { throw "The Desktop folder is unavailable: $desktop" }

    $required = @(
        'Start Spektrafilm Darkroom for Capture One.cmd', 'Start-Spektrafilm.ps1', 'stocks.json', 'panel\NativePanel.dll', 'assets\Spektrafilm.ico',
        'profiles\preview\Linear Rec.2020.icc', 'profiles\preview\ProPhoto RGB.icc',
        'renderer\SpektraStillRenderer.exe', 'renderer\SpektraHanatos2025Spectra.f32', 'renderer\SpektraOutputGamutCompression.f32', 'renderer\SpektraOutputGamutCam16.f32'
    )
    foreach ($shader in @('Copy','CurveDevelop','Diffusion','Dir','FilmExposure','FormatConvert','Grain','Halation','PrintScan','ScannerPost')) {
        $required += "renderer\shaders\Spektra$shader.comp.spv"
    }
    $optional = @(
        'Install Spektrafilm Darkroom for Capture One.cmd', 'Install-Spektrafilm.ps1', 'Setup LUT Export.cmd',
        'Setup-LutExport.ps1', 'lut_worker.py', 'requirements-lut.txt',
        'README.md', 'LICENSE', 'SOURCE.txt', 'THIRD-PARTY.md', 'UPSTREAM-CHANGES.md', 'assets\Spektrafilm.png',
        'upstream-ofx\LICENSE.txt', 'upstream-ofx\Legal\THIRD_PARTY_NOTICES.txt',
        'upstream-ofx\Legal\SPEKTRAFILM_OFX_LICENSE.txt', 'upstream-ofx\Legal\SPEKTRAFILM_OFX_LUT_LICENSE.txt'
    )
    $files = @()
    foreach ($relative in $required + $optional) {
        $origin = Join-Path $source $relative
        if (!(Test-Path -LiteralPath $origin -PathType Leaf)) {
            if ($required -contains $relative) { throw "The extracted release is missing $relative." }
            continue
        }
        $item = Get-Item -LiteralPath $origin
        Confirm-PlainItem $item
        if ($item.Length -eq 0) { throw "The extracted release contains an empty $relative." }
        $files += $relative
    }
    foreach ($directory in @('licenses', 'upstream-spektrafilm', 'profiles\export', 'manual')) {
        $optionalTree = Join-Path $source $directory
        if (!(Test-Path -LiteralPath $optionalTree -PathType Container)) { continue }
        Confirm-PlainItem (Get-Item -LiteralPath $optionalTree)
        foreach ($item in Get-ChildItem -LiteralPath $optionalTree -Recurse -File) {
            if ($item.Extension -in '.pyc', '.pyo', '.pyd', '.exe', '.dll' -or $item.FullName -match '[\\/]__pycache__[\\/]') { continue }
            Confirm-PlainItem $item
            $files += $item.FullName.Substring($source.Length + 1)
        }
    }

    $upgrade = Test-Path -LiteralPath $install
    if ($upgrade -and !(Test-OwnedInstall $install)) {
        throw "The destination already exists and is not a $appName installation: $install"
    }
    $shortcutPath = Join-Path $desktop "$appName.lnk"
    if (!$PSCmdlet.ShouldProcess("$install and $shortcutPath", "Install $appName and create its Desktop shortcut")) { return }
    if ($upgrade) { Confirm-UpdateFilesAvailable $install }

    [void] [IO.Directory]::CreateDirectory($parent)
    $id = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $parent ('.film-tools-stage-' + $id)
    $previous = Join-Path $parent ('.film-tools-previous-' + $id)
    $movedPrevious = $false
    $installed = $false
    $keepStage = $false
    $movedUserItems = @()
    try {
        [void] [IO.Directory]::CreateDirectory($stage)
        foreach ($relative in $files) {
            $target = Join-Path $stage $relative
            [void] [IO.Directory]::CreateDirectory((Split-Path -LiteralPath $target))
            [IO.File]::Copy((Join-Path $source $relative), $target)
        }
        [IO.File]::WriteAllText((Join-Path $stage $markerName), $markerText)
        if ($upgrade) {
            try {
                [IO.Directory]::Move($install, $previous)
            } catch {
                if (Test-FileInUse $_.Exception) {
                    throw $lockedMessage
                }
                if (($_.Exception.GetBaseException().HResult -band 0xffff) -eq 5) {
                    throw "Could not replace the installation folder. Close $appName to update. If it is already closed, check that you can write to the installation folder."
                }
                throw
            }
            $movedPrevious = $true
            foreach ($oldItem in Get-ChildItem -LiteralPath $previous -Force) {
                if (Test-Path -LiteralPath (Join-Path $stage $oldItem.Name)) { continue }
                Confirm-PlainItem $oldItem
                Move-Item -LiteralPath $oldItem.FullName -Destination (Join-Path $stage $oldItem.Name)
                $movedUserItems += $oldItem.Name
            }
        }
        [IO.Directory]::Move($stage, $install)
        $installed = $true
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        Save-DesktopShortcut $shortcutPath $powershell (Join-Path $install 'Start-Spektrafilm.ps1') $install $upgrade
    } catch {
        $installError = $_
        try {
            if ($installed) { [IO.Directory]::Move($install, $stage) }
            if ($movedPrevious) {
                foreach ($name in $movedUserItems) {
                    Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $previous $name)
                }
                [IO.Directory]::Move($previous, $install)
            }
        } catch {
            $keepStage = $true
            throw "Installation failed and could not be rolled back. Existing files are retained under $parent. $($installError.Exception.Message) Rollback: $($_.Exception.Message)"
        }
        throw $installError
    } finally {
        if (!$keepStage) { Remove-TemporaryInstall $stage $parent }
    }
    if ($movedPrevious) {
        try { Remove-TemporaryInstall $previous $parent }
        catch { Write-Warning "An older installation remains at $previous." }
    }
    Write-Output "Installed to $install"
    Write-Output "Desktop shortcut created at $shortcutPath"
} catch {
    [Console]::Error.WriteLine("${appName}: $($_.Exception.Message)")
    exit 1
}
