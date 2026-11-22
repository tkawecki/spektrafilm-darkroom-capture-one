[CmdletBinding()]
param(
    [string] $CatalogPath,
    [string] $CaptureOnePath,
    [switch] $CheckOnly,
    [switch] $Shortcut,
    [switch] $AllDocuments,
    [string] $TestDocumentRoot
)

$ErrorActionPreference = 'Stop'
$appName = 'Spektrafilm Darkroom for Capture One'
$userData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FilmToolsForCaptureOne'

function Get-CaptureOneExecutable {
    if ($CaptureOnePath) {
        $candidates = @($CaptureOnePath)
    } else {
        $candidates = @(Join-Path $env:ProgramFiles 'Capture One\Capture One\CaptureOne.exe')
        $keys = @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\CaptureOne.exe',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\CaptureOne.exe'
        )
        foreach ($key in $keys) {
            if (Test-Path -LiteralPath $key) {
                $candidate = (Get-Item -LiteralPath $key).GetValue('')
                if ($candidate) { $candidates += $candidate }
            }
        }
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }
    if ($CaptureOnePath -or $CheckOnly) { throw 'Capture One could not be found. Pass -CaptureOnePath with the path to CaptureOne.exe.' }
    return Select-ExistingFile 'Locate CaptureOne.exe' 'Capture One executable|CaptureOne.exe'
}

function Select-ExistingFile([string] $title, [string] $filter) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    try {
        $dialog.Title = $title
        $dialog.Filter = $filter
        $dialog.RestoreDirectory = $true
        $dialog.CheckFileExists = $true
        $dialog.CheckPathExists = $true
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.FileName }
    } finally {
        $dialog.Dispose()
    }
}

function Confirm-RequiredFile([string] $path) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "A release file is missing: $path. Extract the full release ZIP before starting $appName."
    }
}

function Confirm-CatalogIsIdle([string] $path) {
    if (Get-Process -Name CaptureOne -ErrorAction SilentlyContinue) {
        throw "Close every Capture One window before starting $appName."
    }
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath "$path$suffix") {
            throw "Capture One left catalog transaction files. Open the catalog in Capture One normally, close it, then launch $appName again."
        }
    }
}

function Get-CatalogPath([string] $path) {
    $item = Get-Item -LiteralPath $path
    if ($item.PSIsContainer) {
        $databases = @(Get-ChildItem -LiteralPath $item.FullName -File | Where-Object { $_.Extension -in @('.cocatalogdb','.cosessiondb') })
        if ($databases.Count -ne 1) { throw 'Choose a catalog bundle containing one .cocatalogdb file.' }
        $item = $databases[0]
    }
    if ($item.Extension -notin @('.cocatalogdb','.cosessiondb')) { throw 'Choose an existing Capture One .cocatalogdb or .cosessiondb database.' }
    if (![string]::Equals($item.FullName, (Resolve-Path -LiteralPath $item.FullName).ProviderPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose the original catalog database path.'
    }
    return $item.FullName
}

function Save-CatalogBackup([string] $path) {
    Confirm-CatalogIsIdle $path
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $encoded = [System.Text.Encoding]::UTF8.GetBytes($path.ToUpperInvariant())
        $key = ([BitConverter]::ToString($sha.ComputeHash($encoded))).Replace('-', '').Substring(0, 20)
    } finally {
        $sha.Dispose()
    }
    $folder = Join-Path (Join-Path $userData 'Catalog backups') $key
    [System.IO.Directory]::CreateDirectory($folder) | Out-Null
    $tempPath = Join-Path $folder ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        try {
            $inputDb = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        } catch [IO.IOException] {
            throw "The catalog is busy. Close the program using it before starting $appName. $($_.Exception.Message)"
        }
        try {
            Confirm-CatalogIsIdle $path
            $header = New-Object byte[] 16
            if ($inputDb.Read($header, 0, $header.Length) -ne $header.Length -or
                [Text.Encoding]::ASCII.GetString($header) -cne "SQLite format 3`0") {
                throw 'The selected catalog database does not have a valid SQLite header.'
            }
            $inputDb.Position = 0
            $outputDb = [IO.File]::Open($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputDb.CopyTo($outputDb); $outputDb.Flush($true) }
            finally { $outputDb.Dispose() }
            Confirm-CatalogIsIdle $path
        } finally {
            $inputDb.Dispose()
        }
        $name = [IO.Path]::GetFileNameWithoutExtension($path)
        $existing = @(Get-ChildItem -LiteralPath $folder -Filter ('*' + [IO.Path]::GetExtension($path)) -File | Sort-Object LastWriteTimeUtc -Descending)
        if ($existing.Count -gt 0 -and $existing[0].Length -eq (Get-Item -LiteralPath $tempPath).Length -and
            (Get-FileHash -LiteralPath $tempPath -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $existing[0].FullName -Algorithm SHA256).Hash) {
            return $existing[0].FullName
        }
        $time = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff', [Globalization.CultureInfo]::InvariantCulture)
        $destination = Join-Path $folder ("$name-$time" + [IO.Path]::GetExtension($path))
        [IO.File]::Move($tempPath, $destination)
        foreach ($older in @($existing | Select-Object -Skip 4)) {
            try { Remove-Item -LiteralPath $older.FullName -ErrorAction Stop }
            catch { Write-Warning "Could not remove an older backup at $($older.FullName)." }
        }
        return $destination
    } finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath }
    }
}

# Saved looks and Viewer preferences are copied before each start, so a panel update or rollback can be
# undone without losing them (joint catalog/look backups: nothing-complex/spektrafilm-for-capture-one
# 285f0aa, GPL-3.0, adapted). Each generation is one ZIP with a SHA-256 manifest, which avoids long
# nested paths. A generation identical to the newest one is not repeated; five are kept.
function Save-LookBackup {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $sources = @()
    $looks = Join-Path $userData 'Variant looks'
    if (Test-Path -LiteralPath $looks -PathType Container) {
        $looksFull = (Resolve-Path -LiteralPath $looks).ProviderPath.TrimEnd('\')
        $sources += @(Get-ChildItem -LiteralPath $looksFull -Recurse -File | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName; Entry = 'Variant looks/' + $_.FullName.Substring($looksFull.Length + 1).Replace('\', '/') } })
    }
    $preferences = Join-Path $userData 'Panel preferences.json'
    if (Test-Path -LiteralPath $preferences -PathType Leaf) {
        $sources += [pscustomobject]@{ Path = $preferences; Entry = 'Panel preferences.json' }
    }
    if ($sources.Count -eq 0) { return $null }
    $sources = @($sources | Sort-Object Entry)
    $manifest = (@($sources | ForEach-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash + ' ' + $_.Entry }) -join "`n") + "`n"
    $root = Join-Path $userData 'Look backups'
    [IO.Directory]::CreateDirectory($root) | Out-Null
    foreach ($stale in @(Get-ChildItem -LiteralPath $root -Filter '*.zip.tmp' -File)) {
        Remove-Item -LiteralPath $stale.FullName -Force -ErrorAction SilentlyContinue
    }
    $existing = @(Get-ChildItem -LiteralPath $root -Filter '*.zip' -File | Sort-Object Name -Descending)
    if ($existing.Count -gt 0 -and (Read-LookBackupManifest $existing[0].FullName) -ceq $manifest) { return $existing[0].FullName }
    $time = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff', [Globalization.CultureInfo]::InvariantCulture)
    $destination = Join-Path $root ($time + '.zip')
    $temp = $destination + '.tmp'
    try {
        $archive = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($item in $sources) {
                [void] [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.Path, $item.Entry, [IO.Compression.CompressionLevel]::Optimal)
            }
            $writer = New-Object IO.StreamWriter($archive.CreateEntry('manifest.txt').Open(), (New-Object Text.UTF8Encoding($false)))
            try { $writer.Write($manifest) } finally { $writer.Dispose() }
        } finally { $archive.Dispose() }
        if ((Read-LookBackupManifest $temp -Verify) -cne $manifest) { throw 'A saved look changed while it was being backed up. Start again.' }
        [IO.File]::Move($temp, $destination)
    } catch {
        throw "Could not back up the saved Spektrafilm looks, so Capture One was not started: $($_.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
    foreach ($older in @($existing | Select-Object -Skip 4)) {
        try { Remove-Item -LiteralPath $older.FullName -ErrorAction Stop }
        catch { Write-Warning "Could not remove an older look backup at $($older.FullName)." }
    }
    return $destination
}

# Returns the manifest of a look backup. With -Verify it is rebuilt from the archived bytes instead,
# so it only matches the stored manifest when every entry is complete.
function Read-LookBackupManifest([string] $path, [switch] $Verify) {
    try { $archive = [IO.Compression.ZipFile]::OpenRead($path) } catch { return $null }
    try {
        if (!$Verify) {
            $entry = $archive.GetEntry('manifest.txt')
            if (!$entry) { return $null }
            $reader = New-Object IO.StreamReader($entry.Open())
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $lines = foreach ($entry in @($archive.Entries | Where-Object { $_.FullName -ne 'manifest.txt' } | Sort-Object FullName)) {
                $stream = $entry.Open()
                try { ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') + ' ' + $entry.FullName } finally { $stream.Dispose() }
            }
        } finally { $sha.Dispose() }
        return (@($lines) -join "`n") + "`n"
    } finally { $archive.Dispose() }
}

function Confirm-VulkanRenderer([string] $renderer) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $renderer
    $start.Arguments = '1 1'
    $start.WorkingDirectory = Split-Path -LiteralPath $renderer
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void] $start.EnvironmentVariables.Remove('DOTNET_STARTUP_HOOKS')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = New-Object IO.MemoryStream
        try {
            $stdout = $process.StandardOutput.BaseStream.CopyToAsync($output)
            $stderr = $process.StandardError.ReadToEndAsync()
            $pixel = New-Object byte[] 16
            [Array]::Copy([BitConverter]::GetBytes([single] 1), 0, $pixel, 12, 4)
            $process.StandardInput.BaseStream.Write($pixel, 0, $pixel.Length)
            $process.StandardInput.Close()
            if (!$process.WaitForExit(20000)) { throw 'The Vulkan renderer did not return a test pixel in 20 seconds.' }
            [void] $stdout.GetAwaiter().GetResult()
            $detail = $stderr.GetAwaiter().GetResult().Trim()
            if ($process.ExitCode -ne 0 -or $output.Length -ne 16) {
                throw "The Vulkan renderer could not run on this computer. $detail"
            }
            for ($offset = 0; $offset -lt 16; $offset += 4) {
                $value = [BitConverter]::ToSingle($output.GetBuffer(), $offset)
                if ([single]::IsNaN($value) -or [single]::IsInfinity($value)) { throw 'The Vulkan renderer returned an invalid test pixel.' }
            }
        } finally {
            $output.Dispose()
        }
    } finally {
        if (!$process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

try {
    if (![Environment]::Is64BitOperatingSystem) { throw "$appName requires 64-bit Windows." }
    $app = Get-CaptureOneExecutable
    if (!$app) { return }
    if ([IO.Path]::GetFileName($app) -ine 'CaptureOne.exe') { throw 'Select CaptureOne.exe from the Capture One installation.' }
    $appDirectory = Split-Path -LiteralPath $app
    if (!(Test-Path -LiteralPath (Join-Path $appDirectory 'CaptureOne.dll') -PathType Leaf)) {
        throw 'CaptureOne.dll was not found beside CaptureOne.exe. Select a complete Capture One installation.'
    }
    # The panel uses internal interfaces of one Capture One build. Other builds are not blocked, but the user is told.
    $testedVersion = '16.9.0.3894'
    $foundVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion
    $versionWarning = $null
    if ($foundVersion -ne $testedVersion) {
        $versionWarning = "$appName is made for Capture One $testedVersion. This computer has Capture One $foundVersion. " +
            'The panel may not appear, or may not work correctly, with this version.'
    }

    $hook = Join-Path $PSScriptRoot 'panel\NativePanel.dll'
    $renderer = Join-Path $PSScriptRoot 'renderer\SpektraStillRenderer.exe'
    Confirm-RequiredFile $hook
    Confirm-RequiredFile $renderer
    foreach ($file in @('stocks.json', 'profiles\preview\Linear Rec.2020.icc', 'profiles\preview\ProPhoto RGB.icc',
        'renderer\SpektraHanatos2025Spectra.f32', 'renderer\SpektraOutputGamutCompression.f32')) {
        Confirm-RequiredFile (Join-Path $PSScriptRoot $file)
    }
    foreach ($shader in @('Copy','CurveDevelop','Diffusion','Dir','FilmExposure','FormatConvert','Grain','Halation','PrintScan','ScannerPost')) {
        Confirm-RequiredFile (Join-Path $PSScriptRoot "renderer\shaders\Spektra$shader.comp.spv")
    }

    $python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
    if (!(Test-Path -LiteralPath $python -PathType Leaf)) { $python = $null }
    if ($CheckOnly) {
        Confirm-VulkanRenderer $renderer
        Write-Output 'Capture One files found. Release files and Vulkan render passed.'
        if ($versionWarning) { Write-Warning $versionWarning }
        else { Write-Output "Capture One $foundVersion is the tested version." }
        if ($python) { Write-Output "Optional LUT Python found at $python" }
        else { Write-Output 'Optional LUT Python is not installed.' }
        return
    }

    if (Get-Process -Name CaptureOne -ErrorAction SilentlyContinue) { throw "Close every Capture One window before starting $appName." }
    if ($versionWarning) {
        Write-Warning $versionWarning
        if ($Shortcut) {
            # Started from the desktop shortcut there is no console to read: ask before going on.
            Add-Type -AssemblyName System.Windows.Forms
            $answer = [System.Windows.Forms.MessageBox]::Show("$versionWarning`n`nStart Capture One anyway?", $appName,
                [System.Windows.Forms.MessageBoxButtons]::OKCancel, [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($answer -ne [System.Windows.Forms.DialogResult]::OK) { return }
        }
    }
    if (!$CatalogPath) { $CatalogPath = Select-ExistingFile 'Choose a Capture One catalog or session' 'Capture One catalog or session|*.cocatalogdb;*.cosessiondb' }
    if (!$CatalogPath) { return }
    $database = Get-CatalogPath $CatalogPath
    $backup = Save-CatalogBackup $database
    $lookBackup = Save-LookBackup
    Confirm-CatalogIsIdle $database
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $app
    # Windows file paths cannot contain double quotes and this is always a file, not a trailing directory path.
    $start.Arguments = '"' + $database + '"'
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $appDirectory
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name -like 'SPEKTRAFILM_VERIFY_*' -or $name -like 'SPEKTRAFILM_TEST_*') {
            [void] $start.EnvironmentVariables.Remove($name)
        }
    }
    $start.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] = $hook
    $start.EnvironmentVariables['SPEKTRAFILM_ROOT'] = $PSScriptRoot
    $start.EnvironmentVariables['SPEKTRAFILM_CATALOG'] = $database
    $start.EnvironmentVariables['SPEKTRAFILM_DOCUMENT_MODE'] = $(if ($AllDocuments) { 'all' } else { 'single' })
    if ($TestDocumentRoot) {
        $testRootFull = (Resolve-Path -LiteralPath $TestDocumentRoot).ProviderPath.TrimEnd('\') + '\'
        if (!$database.StartsWith($testRootFull,[StringComparison]::OrdinalIgnoreCase)) { throw 'The initial document must be inside the test folder.' }
        $start.EnvironmentVariables['SPEKTRAFILM_DOCUMENT_ROOT'] = $testRootFull
    } else { [void] $start.EnvironmentVariables.Remove('SPEKTRAFILM_DOCUMENT_ROOT') }
    $start.EnvironmentVariables['SPEKTRAFILM_PANEL_LOG'] = Join-Path $userData 'panel.log'
    if ($python) { $start.EnvironmentVariables['SPEKTRAFILM_PYTHON'] = $python }
    else { [void] $start.EnvironmentVariables.Remove('SPEKTRAFILM_PYTHON') }
    $process = [Diagnostics.Process]::Start($start)
    try { Write-Output "Started Capture One ($($process.Id)). Catalog backup: $backup. Look backup: $(if ($lookBackup) { $lookBackup } else { 'no saved looks yet' })" }
    finally { $process.Dispose() }
} catch {
    $message = $_.Exception.Message
    [Console]::Error.WriteLine("${appName}: $message")
    if ($Shortcut) {
        Add-Type -AssemblyName System.Windows.Forms
        [void] [System.Windows.Forms.MessageBox]::Show($message, $appName,
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
    }
    exit 1
}
