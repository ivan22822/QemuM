# Standalone Windows PowerShell 5.1+ diagnostic collector. No downloads or repairs.
[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA 'QemuM Alpha'),
    [string]$OutputDirectory = '',
    [ValidateRange(1, 60)][int]$ObserveSeconds = 15,
    [ValidateRange(1, 48)][int]$EventHours = 4
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$issues = New-Object 'System.Collections.Generic.List[string]'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$installRoot = [IO.Path]::GetFullPath($InstallPath)

function Protect-Text([string]$Text) {
    if ($null -eq $Text) { return '' }
    $value = $Text -replace '(?i)([a-z]:[\\/]+Users[\\/]+)[^\\/\r\n"]+', '$1<user>'
    $value = $value -replace '(?i)(https?://)[^/\s@]+@', '$1<credentials>@'
    $value = $value -replace '(?i)\b(password|passwd|secret|token|api[_-]?key)\s*[=:]\s*[^\s,;]+', '$1=<redacted>'
    return $value
}

function Write-ReportJson([string]$Name, $Value) {
    # JSON escaping is preserved: replace only profile-folder names in serialized paths.
    $json = ConvertTo-Json -InputObject $Value -Depth 12
    $json = $json -replace '(?i)([a-z]:[\\/]+Users[\\/]+)[^\\/"]+', '$1<user>'
    # Refuse to write malformed JSON if sanitization ever encounters unusual data.
    $null = ConvertFrom-Json -InputObject $json
    [IO.File]::WriteAllText((Join-Path $reportDir $Name), $json, $utf8)
}

function File-Record([string]$Path) {
    if (-not [IO.File]::Exists($Path)) {
        return [pscustomobject]@{ path = $Path; exists = $false }
    }
    try {
        $file = Get-Item -LiteralPath $Path
        return [pscustomobject]@{
            path = $file.FullName; exists = $true; bytes = $file.Length
            modified_utc = $file.LastWriteTimeUtc.ToString('o')
            file_version = if ($file.Extension -eq '.exe') { $file.VersionInfo.FileVersion } else { $null }
        }
    } catch {
        $issues.Add("File metadata: $(Protect-Text $_.Exception.Message)")
        return [pscustomobject]@{ path = $Path; error = 'Metadata unavailable' }
    }
}

function Installer-Processes {
    try {
        $all = @(Get-CimInstance Win32_Process -OperationTimeoutSec 8)
        $selected = @($all | Where-Object { $_.Name -eq 'qemu-setup.exe' -or $_.Name -like 'QemuM*Setup*' })
        # Include installer children (e.g. NSIS helpers), but never export command lines.
        for ($level = 0; $level -lt 4; $level++) {
            $ids = @($selected | ForEach-Object { $_.ProcessId })
            $known = @($selected | ForEach-Object { $_.ProcessId })
            $children = @($all | Where-Object { $ids -contains $_.ParentProcessId -and $known -notcontains $_.ProcessId })
            if ($children.Count -eq 0) { break }
            $selected += $children
        }
        return @($selected | ForEach-Object {
            [pscustomobject]@{
                name = $_.Name; pid = [int]$_.ProcessId; parent_pid = [int]$_.ParentProcessId
                created = if ($_.CreationDate) { $_.CreationDate.ToUniversalTime().ToString('o') } else { '' }
                executable = $_.ExecutablePath
                cpu_seconds = ([double]$_.KernelModeTime + [double]$_.UserModeTime) / 10000000
                read_bytes = [double]$_.ReadTransferCount; write_bytes = [double]$_.WriteTransferCount
            }
        })
    } catch {
        $issues.Add("Process inventory: $(Protect-Text $_.Exception.Message)")
        return @()
    }
}

function Recent-QemuFiles([string[]]$Folders) {
    $records = @()
    foreach ($folder in $Folders) {
        if (-not [IO.Directory]::Exists($folder)) { continue }
        try {
            $records += @(Get-ChildItem -LiteralPath $folder -File |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 12 |
                ForEach-Object { File-Record $_.FullName })
        } catch { $issues.Add("Folder metadata: $(Protect-Text $_.Exception.Message)") }
    }
    return $records
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($desktop)) { $desktop = [IO.Path]::GetTempPath() }
    $OutputDirectory = Join-Path $desktop 'QemuM-Diagnostics'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $outputRoot -Force
$reportName = 'QemuM-report-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$reportDir = Join-Path $outputRoot $reportName
$null = New-Item -ItemType Directory -Path $reportDir

Write-Host 'QemuM installation diagnostics'
Write-Host 'Read-only collection. No VM configuration, disks or logs are copied.'
Write-Host "Install folder: $installRoot"

$candidates = @(
    (Join-Path $env:ProgramFiles 'QemuM-QEMU-20260811'),
    (Join-Path $env:ProgramFiles 'qemu')
)
try {
    $candidates += @(Get-Command qemu-system-x86_64.exe -CommandType Application -All -ErrorAction SilentlyContinue |
        ForEach-Object { Split-Path -Parent $_.Source })
    foreach ($key in @('HKLM:\SOFTWARE\QEMU', 'HKLM:\SOFTWARE\qemu64')) {
        $property = Get-ItemProperty -LiteralPath $key -Name Install_Dir -ErrorAction SilentlyContinue
        if ($property.Install_Dir) { $candidates += [string]$property.Install_Dir }
    }
} catch { $issues.Add("QEMU discovery: $(Protect-Text $_.Exception.Message)") }

$runtimePath = [IO.Path]::Combine($installRoot, 'runtime.json')
if (Test-Path -LiteralPath $runtimePath -PathType Leaf) {
    try {
        if ((Get-Item -LiteralPath $runtimePath).Length -gt 1048576) { throw 'runtime.json exceeds 1 MB' }
        $runtime = Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($runtime.qemu_dir) { $candidates += [string]$runtime.qemu_dir }
    } catch { $issues.Add("Runtime settings: $(Protect-Text $_.Exception.Message)") }
}
$candidates = @($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
$watched = @(
    ([IO.Path]::Combine($installRoot, 'downloads\qemu-setup.exe')),
    ([IO.Path]::Combine($installRoot, 'downloads\python.zip')),
    ([IO.Path]::Combine($installRoot, 'downloads\app.zip')),
    ([IO.Path]::Combine($installRoot, 'runtime\python\python.exe')),
    ([IO.Path]::Combine($installRoot, 'QemuM.exe')),
    ([IO.Path]::Combine($installRoot, 'active.json')),
    $runtimePath
)
foreach ($candidate in $candidates) {
    $watched += [IO.Path]::Combine($candidate, 'qemu-system-x86_64.exe')
    $watched += [IO.Path]::Combine($candidate, 'qemu-img.exe')
}

Write-Host "Observing installation activity for $ObserveSeconds seconds..."
$firstTime = [DateTime]::UtcNow
$first = @(Installer-Processes)
$filesBefore = @($watched | ForEach-Object { File-Record $_ })
$recentBefore = @(Recent-QemuFiles $candidates)
Start-Sleep -Seconds $ObserveSeconds
$second = @(Installer-Processes)
$filesAfter = @($watched | ForEach-Object { File-Record $_ })
$recentAfter = @(Recent-QemuFiles $candidates)
$elapsed = ([DateTime]::UtcNow - $firstTime).TotalSeconds
$activity = @()
foreach ($process in $second) {
    $previous = $first | Where-Object { $_.pid -eq $process.pid -and $_.created -eq $process.created } | Select-Object -First 1
    if ($previous) {
        $activity += [pscustomobject]@{
            name = $process.name; pid = $process.pid
            cpu_seconds_added = [Math]::Round($process.cpu_seconds - $previous.cpu_seconds, 3)
            read_bytes_added = $process.read_bytes - $previous.read_bytes
            write_bytes_added = $process.write_bytes - $previous.write_bytes
        }
    }
}
Write-ReportJson 'processes.json' ([ordered]@{
    observation_seconds = [Math]::Round($elapsed, 1); before = $first; after = $second; activity = $activity
    note = 'No command lines collected. No activity alone does not prove a hang.'
})
Write-ReportJson 'files.json' ([ordered]@{
    before = $filesBefore; after = $filesAfter; recent_qemu_files_before = $recentBefore
    recent_qemu_files_after = $recentAfter
})

Write-Host 'Checking the downloaded installer checksum, if the file is stable...'
$download = [IO.Path]::Combine($installRoot, 'downloads\qemu-setup.exe')
$checksum = [ordered]@{ reference = 'QEMU 20260811 pinned by QemuM Alpha 1/2'; status = 'file_not_found' }
if (Test-Path -LiteralPath $download -PathType Leaf) {
    try {
        $before = Get-Item -LiteralPath $download
        $earlier = $filesBefore | Where-Object { $_.path -eq $download } | Select-Object -First 1
        if (-not $earlier.exists -or $earlier.bytes -ne $before.Length -or
            $earlier.modified_utc -ne $before.LastWriteTimeUtc.ToString('o')) {
            $checksum.status = 'changing_file_not_hashed'
        } else {
            $expected = '5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543'
            $size = $before.Length; $modified = $before.LastWriteTimeUtc
            $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA512).Hash
            $after = Get-Item -LiteralPath $download
            if ($after.Length -ne $size -or $after.LastWriteTimeUtc -ne $modified) {
                $checksum.status = 'changed_while_hashing'
            } else {
                $checksum.status = 'hashed'; $checksum.matches_pinned_reference = ($actual -eq $expected)
                $checksum.bytes = $size; $checksum.sha512 = $actual
            }
        }
    } catch { $checksum.status = 'unavailable'; $issues.Add("Checksum: $(Protect-Text $_.Exception.Message)") }
}
Write-ReportJson 'checksum.json' $checksum

$events = @()
Write-Host 'Reading recent Windows events mentioning QEMU...'
foreach ($log in @('Application', 'Microsoft-Windows-Windows Defender/Operational')) {
    try {
        $items = @(Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = (Get-Date).AddHours(-$EventHours) } -MaxEvents 200 -ErrorAction Stop)
        $events += @($items | Where-Object { $_.Message -match '(?i)qemu' } | Select-Object -First 30 | ForEach-Object {
            $message = [string]$_.Message
            if ($message.Length -gt 6000) { $message = $message.Substring(0, 6000) + ' [truncated]' }
            [pscustomobject]@{
                log = $log; time_utc = $_.TimeCreated.ToUniversalTime().ToString('o')
                id = $_.Id; provider = $_.ProviderName; level = $_.LevelDisplayName
                message = Protect-Text $message
            }
        })
    } catch { $issues.Add("Events ($log): $(Protect-Text $_.Exception.Message)") }
}
Write-ReportJson 'events.json' $events

$system = [ordered]@{
    created_utc = [DateTime]::UtcNow.ToString('o'); schema = 1
    powershell = $PSVersionTable.PSVersion.ToString(); clr = [Environment]::Version.ToString()
    os_64bit = [Environment]::Is64BitOperatingSystem; process_64bit = [Environment]::Is64BitProcess
    install_path = $installRoot; qemu_candidates = $candidates
    collection_errors = @($issues)
    privacy = 'No VM configurations, media, guest screenshots, raw logs, command lines, username or hostname fields collected. Profile paths masked.'
}
try {
    $os = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 8
    $system.windows = [ordered]@{ caption = $os.Caption; version = $os.Version; build = $os.BuildNumber; free_memory_mb = [Math]::Round($os.FreePhysicalMemory / 1024) }
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($installRoot).Substring(0, 1))
    $system.install_drive_free_gb = [Math]::Round($drive.Free / 1GB, 2)
} catch { $system.system_info_error = Protect-Text $_.Exception.Message }
Write-ReportJson 'report.json' $system

$summary = @(
    'QemuM installation diagnostics',
    '',
    "Observed for approximately $([Math]::Round($elapsed, 1)) seconds.",
    "Installer processes: before=$($first.Count), after=$($second.Count).",
    "Downloaded QEMU checksum status: $($checksum.status).",
    '',
    'Read report.json, processes.json, files.json, checksum.json and events.json.',
    'CPU/I-O counters and changing files help distinguish activity from waiting.',
    'Zero activity does not prove a hang. Check for a Windows elevation prompt.',
    'A checksum mismatch against the pinned reference can mean an incomplete download or a different installer version.',
    'Missing events or access errors are recorded; they do not prove that no error occurred.',
    '',
    'Before sharing, add: exact stage text, how long it was stuck, installer version and a screenshot.',
    'Review the report first. Event messages can still contain private details despite best-effort redaction.',
    'Nothing was uploaded. No installation, repair, VM launch, process termination or settings change was performed.'
)
[IO.File]::WriteAllLines((Join-Path $reportDir 'README.txt'), $summary, $utf8)
$archive = Join-Path $outputRoot ($reportName + '.zip')
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($reportDir, $archive)
    Write-Host ''
    Write-Host 'DONE. Review this ZIP, then send it back:'
    Write-Host $archive -ForegroundColor Green
} catch {
    Write-Warning "ZIP creation failed. The report folder is preserved: $reportDir"
    Write-Warning (Protect-Text $_.Exception.Message)
}
