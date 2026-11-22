# Tests Save-LookBackup from Start-Spektrafilm.ps1 on a scratch user-data folder. Does not start Capture One
# and never touches the real FilmToolsForCaptureOne folder. Windows PowerShell 5.1 or PowerShell 7.
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Start-Spektrafilm.ps1'), [ref] $tokens, [ref] $errors)
if ($errors.Count) { throw "Launcher does not parse: $($errors[0].Message)" }
foreach ($name in 'Save-LookBackup', 'Read-LookBackupManifest') {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (!$function) { throw "$name not found." }
    Invoke-Expression $function.Extent.Text
}
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$userData = Join-Path ([IO.Path]::GetTempPath()) ('spektrafilm-look-backup-' + [Guid]::NewGuid().ToString('N'))
$assertions = 0
function Check([bool] $ok, [string] $why) { $script:assertions++; if (!$ok) { throw $why } }
function Generations { @(Get-ChildItem -LiteralPath (Join-Path $userData 'Look backups') -Filter '*.zip' -File | Sort-Object Name) }
function EntryText([string] $zip, [string] $entry) {
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $item = $archive.GetEntry($entry); if (!$item) { return $null }
        $reader = New-Object IO.StreamReader($item.Open()); try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
}
try {
    New-Item -ItemType Directory -Force $userData | Out-Null
    Check ($null -eq (Save-LookBackup)) 'no looks must mean no backup'

    $catalog = Join-Path $userData ('Variant looks\' + ('A' * 64))
    $versions = Join-Path $catalog ('Versions\' + ('b' * 32))
    New-Item -ItemType Directory -Force $versions | Out-Null
    [IO.File]::WriteAllText((Join-Path $catalog 'look.json'), '{"Version":1}')
    [IO.File]::WriteAllText((Join-Path $versions (('c' * 60) + '.json')), '{"Version":1,"Name":"one"}')
    [IO.File]::WriteAllText((Join-Path $userData 'Panel preferences.json'), '{"Version":1}')
    [IO.File]::WriteAllText((Join-Path $userData 'panel.log'), 'not part of the backup')
    New-Item -ItemType Directory -Force (Join-Path $userData 'Look backups') | Out-Null
    [IO.File]::WriteAllText((Join-Path $userData 'Look backups\20000101-000000-000.zip.tmp'), 'left by a crashed launcher')

    $first = Save-LookBackup
    Check ((EntryText $first ('Variant looks/' + ('A' * 64) + '/look.json')) -eq '{"Version":1}') 'look not archived exactly'
    Check ((EntryText $first ('Variant looks/' + ('A' * 64) + '/Versions/' + ('b' * 32) + '/' + ('c' * 60) + '.json')) -ne $null) 'nested version not archived'
    Check ((EntryText $first 'Panel preferences.json') -eq '{"Version":1}') 'preferences not archived exactly'
    Check ($null -eq (EntryText $first 'panel.log')) 'log must not be archived'
    Check (((EntryText $first 'manifest.txt') -split "`n" | Where-Object { $_ }).Count -eq 3) 'manifest must list three files'
    Check ((Read-LookBackupManifest $first -Verify) -ceq (EntryText $first 'manifest.txt')) 'archived bytes must match the manifest'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $userData 'Look backups') -Filter '*.tmp').Count -eq 0) 'stale temporary file must be removed'

    Check ((Save-LookBackup) -eq $first) 'unchanged looks must reuse the newest backup'
    Check ((Generations).Count -eq 1) 'unchanged looks must not add a generation'

    Start-Sleep -Milliseconds 20
    [IO.File]::WriteAllText((Join-Path $catalog 'look.json'), '{"Version":1,"Edited":true}')
    $second = Save-LookBackup
    Check ($second -ne $first) 'a changed look must create a new generation'
    Check ((EntryText $first ('Variant looks/' + ('A' * 64) + '/look.json')) -eq '{"Version":1}') 'older generation must stay unchanged'

    for ($i = 0; $i -lt 6; $i++) {
        Start-Sleep -Milliseconds 20
        [IO.File]::WriteAllText((Join-Path $catalog 'look.json'), "{`"Version`":1,`"Edit`":$i}")
        $newest = Save-LookBackup
    }
    $kept = Generations
    Check ($kept.Count -eq 5) "five generations must be kept, found $($kept.Count)"
    Check ($kept[-1].FullName -eq $newest) 'newest generation must be kept'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $userData 'Look backups') -Filter '*.tmp').Count -eq 0) 'no temporary file may remain'
    Write-Output "PASS $assertions launcher look-backup assertions."
} finally {
    if (Test-Path -LiteralPath $userData) { Remove-Item -LiteralPath $userData -Recurse -Force }
}
