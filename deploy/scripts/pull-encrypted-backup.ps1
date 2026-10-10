#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$ProfilePath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$profileModulePath = Join-Path $repositoryRoot 'tools/deployment/Mk8DeploymentProfile.psm1'
Import-Module $profileModulePath -Force
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $repositoryRoot 'deploy/secrets/production-profile.json'
}
$profile = Import-Mk8DeploymentProfile `
    -Path $ProfilePath `
    -RepositoryRoot $repositoryRoot `
    -RequireConnectionFiles
$Target = "root@$($profile.ServerIPv4)"
$KeyPath = $profile.SshKeyPath
$KnownHostsPath = $profile.KnownHostsPath
$Destination = $profile.BackupDestination

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [int]$TimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "$FilePath exceeded its $TimeoutSeconds second limit."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode).`n$stderr"
    }

    return $stdout.Trim()
}

$sshOptions = @(
    '-i', $KeyPath,
    '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=10',
    '-o', 'ServerAliveInterval=10',
    '-o', 'ServerAliveCountMax=2',
    '-o', 'StrictHostKeyChecking=yes',
    '-o', "UserKnownHostsFile=$KnownHostsPath",
    '-o', 'LogLevel=ERROR'
)

$remoteCommand = "timeout 20 bash -lc 'find /var/backups/mk8-export -mindepth 1 -maxdepth 1 -type f -name `"20??????T??????Z.tar.gz.age`" -printf `"%f\\n`" | sort | tail -n 1'"
$backupName = Invoke-BoundedProcess ssh.exe ($sshOptions + @($Target, $remoteCommand)) 60
if ($backupName -notmatch '^[0-9]{8}T[0-9]{6}Z\.tar\.gz\.age$') {
    throw 'The server did not return a valid encrypted backup name.'
}

$destinationRoot = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
$finalBackup = Join-Path $destinationRoot $backupName
$finalChecksum = "$finalBackup.sha256"
if ((Test-Path -LiteralPath $finalBackup) -or (Test-Path -LiteralPath $finalChecksum)) {
    throw 'The local encrypted backup already exists.'
}

$taskRoot = Join-Path ([IO.Path]::GetTempPath()) 'mk8.email/pull-encrypted-backup'
$runRoot = Join-Path $taskRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

try {
    Invoke-BoundedProcess scp.exe ($sshOptions + @(
        "$Target`:/var/backups/mk8-export/$backupName",
        "$Target`:/var/backups/mk8-export/$backupName.sha256",
        $runRoot
    )) 300 | Out-Null

    $downloadedBackup = Join-Path $runRoot $backupName
    $downloadedChecksum = "$downloadedBackup.sha256"
    $checksumLine = (Get-Content -LiteralPath $downloadedChecksum -Raw).Trim()
    $checksumMatch = [regex]::Match(
        $checksumLine,
        "^([0-9a-f]{64})  $([regex]::Escape($backupName))$")
    if (-not $checksumMatch.Success) {
        throw 'The encrypted backup checksum file is not valid.'
    }

    $actualHash = (Get-FileHash -LiteralPath $downloadedBackup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $checksumMatch.Groups[1].Value) {
        throw 'The encrypted backup checksum does not match.'
    }

    Move-Item -LiteralPath $downloadedBackup -Destination $finalBackup
    Move-Item -LiteralPath $downloadedChecksum -Destination $finalChecksum
}
finally {
    $resolvedTaskRoot = [IO.Path]::GetFullPath($taskRoot).TrimEnd('\') + '\'
    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    if ($resolvedRunRoot.StartsWith($resolvedTaskRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedRunRoot)) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}

Write-Host "Stored and verified $finalBackup."
