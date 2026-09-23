#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$ProfilePath,
    [switch]$Activate
)

$ErrorActionPreference = 'Stop'

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [switch]$PassThru
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
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
    if ($stdout) {
        Write-Host $stdout.TrimEnd()
    }
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode).`n$stderr"
    }
    if ($stderr) {
        Write-Host $stderr.TrimEnd()
    }
    if ($PassThru) {
        return $stdout
    }
}

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
$resolvedProfilePath = [IO.Path]::GetFullPath($ProfilePath)
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

$taskRoot = 'D:\temp\mk8.email\deploy-production'
$runRoot = Join-Path $taskRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

try {
    $commit = (Invoke-BoundedProcess git @('rev-parse', 'HEAD') 30 $repositoryRoot -PassThru).Trim()
    if ($commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Git returned an invalid commit identifier.'
    }
    $shortCommit = $commit.Substring(0, 12)
    $remoteBuildOutput = Invoke-BoundedProcess ssh.exe `
        ($sshOptions + @($Target, "timeout 900s /usr/local/sbin/mk8-build '$commit'")) `
        930 $repositoryRoot -PassThru
    $buildRunMatches = [regex]::Matches(
        $remoteBuildOutput,
        "(?m)^BUILD_RUN=(/var/lib/mk8email-build/runs/$shortCommit-[0-9]{8}T[0-9]{6}Z-[0-9]+)\r?$"
    )
    $artifactMatches = [regex]::Matches(
        $remoteBuildOutput,
        '(?m)^ARTIFACT_SHA256=([0-9a-f]{64})\r?$'
    )
    if ($buildRunMatches.Count -ne 1 -or $artifactMatches.Count -ne 1) {
        throw 'The isolated build did not return one validated artifact.'
    }
    $buildRun = $buildRunMatches[0].Groups[1].Value
    $releaseDigest = $artifactMatches[0].Groups[1].Value
    $serverReleaseArchive = "$buildRun/mk8email-linux-x64.tar.gz"

    $assetArchive = Join-Path $runRoot 'mk8email-assets.tar.gz'
    $sourceArchive = Join-Path $runRoot 'source.tar'
    $sourceSnapshotRoot = Join-Path $runRoot 'source'
    [IO.Directory]::CreateDirectory($sourceSnapshotRoot) | Out-Null
    Invoke-BoundedProcess git @('archive', '--format=tar', "--output=$sourceArchive", $commit, 'deploy') 60 $repositoryRoot
    Invoke-BoundedProcess tar.exe @('--extract', "--file=$sourceArchive", "--directory=$sourceSnapshotRoot") 60 $repositoryRoot
    $snapshotSecrets = Join-Path $sourceSnapshotRoot 'deploy\secrets'
    [IO.Directory]::CreateDirectory($snapshotSecrets) | Out-Null
    $snapshotProfile = Join-Path $snapshotSecrets 'production-profile.json'
    [IO.File]::Copy($resolvedProfilePath, $snapshotProfile, $false)
    $renderedAssetsRoot = Join-Path $runRoot 'rendered-assets'
    New-Mk8RenderedDeployAssets `
        -ProfilePath $snapshotProfile `
        -RepositoryRoot $sourceSnapshotRoot `
        -Destination $renderedAssetsRoot | Out-Null
    Invoke-BoundedProcess tar.exe @('--create', '--gzip', "--file=$assetArchive", '--directory', $renderedAssetsRoot, 'deploy') 120 $repositoryRoot
    $assetDigest = (Get-FileHash -LiteralPath $assetArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $releaseMaterial = [Text.Encoding]::ASCII.GetBytes("$releaseDigest`n$assetDigest`n")
    $releaseId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($releaseMaterial)).Substring(0, 16).ToLowerInvariant()

    $deploymentNonce = [Guid]::NewGuid().ToString('N')
    $remoteRoot = "/run/mk8email-deploy-$releaseId-$deploymentNonce"
    $remoteScript = Join-Path $runRoot 'remote-install.sh'
    $activationRequested = if ($Activate) { 'true' } else { 'false' }
    $remoteScriptText = @"
#!/bin/sh
set -eu
remote_root='$remoteRoot'
release_id='$releaseId'
source_commit='$commit'
source_short_commit='$shortCommit'
release_source='$serverReleaseArchive'
release_digest='$releaseDigest'
asset_digest='$assetDigest'
activate_requested='$activationRequested'
was_active=false
rollback_supported=false
changes_started=false
apt_sources_changing=false
backup_path=
previous_release=
apt_source_backup=
configuration_restore=

restore_apt_sources() {
    restore_status=0
    rm -f -- /etc/apt/sources.list \
        /etc/apt/sources.list.d/debian.sources || restore_status=1
    if [ -f "`$apt_source_backup/sources.list" ]; then
        cp --archive "`$apt_source_backup/sources.list" /etc/apt/sources.list \
            || restore_status=1
    fi
    if [ -f "`$apt_source_backup/debian.sources" ]; then
        cp --archive "`$apt_source_backup/debian.sources" \
            /etc/apt/sources.list.d/debian.sources || restore_status=1
    fi
    return "`$restore_status"
}

restore_exact_directory() {
    restore_relative=`$1
    restore_optional=`$2
    restore_source="`$configuration_restore/`$restore_relative"
    restore_destination="/`$restore_relative"
    case "`$restore_relative" in
        etc/mk8email|etc/nginx|etc/rspamd|usr/local/lib/mk8email/tests|var/www/mk8email-domains|usr/local/share/mk8email/domain-templates) ;;
        *) return 1 ;;
    esac
    if [ -L "`$restore_destination" ] \
        || { [ -e "`$restore_destination" ] && [ ! -d "`$restore_destination" ]; }; then
        return 1
    fi
    if [ ! -d "`$restore_source" ] || [ -L "`$restore_source" ]; then
        if [ "`$restore_optional" = true ]; then
            rm -rf -- "`$restore_destination"
            return `$?
        fi
        return 1
    fi
    mkdir -p -- "`$restore_destination" || return 1
    if [ "`$restore_relative" = etc/mk8email ]; then
        rsync --archive --delete --numeric-ids --exclude=bootstrap-secrets \
            "`$restore_source/" "`$restore_destination/"
    else
        rsync --archive --delete --numeric-ids \
            "`$restore_source/" "`$restore_destination/"
    fi
}

restore_configuration() {
    restore_archive="`$backup_path/configuration.tar.gz"
    configuration_restore="`$remote_root/configuration-restore"
    rm -rf -- "`$configuration_restore"
    install -d -o root -g root -m 0700 "`$configuration_restore" || return 1
    tar --extract --gzip --file="`$restore_archive" \
        --directory="`$configuration_restore" || return 1

    restore_status=0
    restore_exact_directory etc/mk8email false || restore_status=1
    restore_exact_directory etc/nginx false || restore_status=1
    restore_exact_directory etc/rspamd false || restore_status=1
    restore_exact_directory usr/local/lib/mk8email/tests false || restore_status=1
    restore_exact_directory var/www/mk8email-domains true || restore_status=1
    restore_exact_directory usr/local/share/mk8email/domain-templates true \
        || restore_status=1

    for restore_file in \
        usr/local/sbin/deploy-mk8-domain-certificate \
        usr/local/sbin/mk8-domain; do
        if [ ! -e "`$configuration_restore/`$restore_file" ]; then
            rm -f -- "/`$restore_file" || restore_status=1
        fi
    done
    tar --extract --gzip --file="`$restore_archive" --directory=/ \
        || restore_status=1
    return "`$restore_status"
}

finish() {
    deployment_status=`$?
    trap - EXIT HUP INT TERM
    if [ "`$deployment_status" -ne 0 ] && [ "`$apt_sources_changing" = true ]; then
        printf '%s\n' "The deployment failed. The prior APT sources will be restored." >&2
        set +e
        restore_apt_sources
        apt_rollback_status=`$?
        if [ "`$apt_rollback_status" -ne 0 ]; then
            printf '%s\n' "APT source rollback failed. Immediate operator action is required." >&2
        fi
        set -e
    fi
    if [ "`$deployment_status" -ne 0 ] && [ "`$changes_started" = true ] && [ "`$was_active" = true ]; then
        set +e
        rollback_status=0
        sh "`$remote_root/assets/deploy/scripts/deactivate-mail-stack" \
            || rollback_status=1
        systemctl mask postfix.service dovecot.service || rollback_status=1
        if [ "`$rollback_supported" = true ]; then
            printf '%s\n' "The deployment failed. The prior native release will be restored." >&2
            if [ "`$rollback_status" -eq 0 ]; then
                restore_configuration || rollback_status=1
            fi
            systemctl mask postfix.service dovecot.service || rollback_status=1
            if [ "`$rollback_status" -eq 0 ]; then
                ln -sfnT "`$previous_release" /opt/mk8email/current \
                    || rollback_status=1
                systemctl daemon-reload || rollback_status=1
                nft -f /etc/nftables.conf || rollback_status=1
                systemctl reload nginx.service || rollback_status=1
                systemctl restart fail2ban.service || rollback_status=1
            fi
            if [ "`$rollback_status" -eq 0 ]; then
                /usr/local/sbin/activate-mail-stack \
                    || rollback_status=1
            fi
            if [ "`$rollback_status" -eq 0 ]; then
                case "`$release_id" in
                    *[!0-9a-f]*|'') ;;
                    *) rm -rf -- "/opt/mk8email/releases/`$release_id" ;;
                esac
            fi
        else
            printf '%s\n' "The deployment failed before a compatible native rollback existed." >&2
            rollback_status=1
        fi
        if [ "`$rollback_status" -ne 0 ]; then
            printf '%s\n' "The native mail service remains inactive. Immediate operator action is required." >&2
        fi
        set -e
    fi
    case "`$remote_root" in
        /run/mk8email-deploy-*) rm -rf -- "`$remote_root" ;;
    esac
    exit "`$deployment_status"
}
trap finish EXIT
trap 'exit 130' HUP INT TERM

deploy_lock=/run/lock/mk8email-deploy.lock
exec 9>"`$deploy_lock"
if ! flock --exclusive --nonblock 9; then
    printf '%s\n' "Another production deployment is running." >&2
    exit 1
fi

case "`$release_source" in
    /var/lib/mk8email-build/runs/`$source_short_commit-????????T??????Z-[0-9]*/mk8email-linux-x64.tar.gz) ;;
    *) printf '%s\n' "The isolated build artifact path is not valid." >&2; exit 1 ;;
esac
build_run=`${release_source%/*}
evidence="`$build_run/evidence.txt"
if [ -L "`$build_run" ] || [ ! -d "`$build_run" ]; then
    printf '%s\n' "The isolated build run is not a physical directory." >&2
    exit 1
fi
[ "`$(readlink -f -- "`$build_run")" = "`$build_run" ] \
    || { printf '%s\n' "The isolated build run does not resolve to itself." >&2; exit 1; }
if [ -L "`$release_source" ] || [ ! -f "`$release_source" ]; then
    printf '%s\n' "The isolated build artifact is not a regular file." >&2
    exit 1
fi
[ "`$(stat -c '%U:%G %a' "`$release_source")" = 'mk8build:mk8build 600' ] \
    || { printf '%s\n' "The isolated build artifact has unsafe metadata." >&2; exit 1; }
if [ -L "`$evidence" ] || [ ! -f "`$evidence" ]; then
    printf '%s\n' "The isolated build evidence is not a regular file." >&2
    exit 1
fi
[ "`$(stat -c '%U:%G %a' "`$evidence")" = 'mk8build:mk8build 600' ] \
    || { printf '%s\n' "The isolated build evidence has unsafe metadata." >&2; exit 1; }
grep -Fx "commit=`$source_commit" "`$evidence" >/dev/null \
    || { printf '%s\n' "The isolated build evidence has the wrong commit." >&2; exit 1; }
grep -Fx "artifact_sha256=`$release_digest" "`$evidence" >/dev/null \
    || { printf '%s\n' "The isolated build evidence has the wrong digest." >&2; exit 1; }
[ "`$(sha256sum "`$release_source" | cut -d' ' -f1)" = "`$release_digest" ] \
    || { printf '%s\n' "The isolated build artifact digest is not valid." >&2; exit 1; }

install -d -o root -g root -m 0700 "`$remote_root" "`$remote_root/assets"
install -o root -g root -m 0600 "`$release_source" \
    "`$remote_root/mk8email-release.tar.gz"
[ "`$(sha256sum "`$remote_root/mk8email-release.tar.gz" | cut -d' ' -f1)" \
    = "`$release_digest" ] \
    || { printf '%s\n' "The copied release artifact digest is not valid." >&2; exit 1; }
tar --extract --gzip --file="`$remote_root/mk8email-assets.tar.gz" --directory="`$remote_root/assets"
chown root:root \
    "`$remote_root/assets/deploy/prerequisites/debian-13-runtime.txt" \
    "`$remote_root/assets/deploy/prerequisites/debian-13-sources.list" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"
chmod 0644 \
    "`$remote_root/assets/deploy/prerequisites/debian-13-runtime.txt" \
    "`$remote_root/assets/deploy/prerequisites/debian-13-sources.list" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"

if [ -f /etc/mk8email/mail-stack-ready ]; then
    was_active=true
    previous_release=`$(readlink -f /opt/mk8email/current)
    case "`$previous_release" in
        /opt/mk8email/releases/*) ;;
        *) printf '%s\n' "The current release path is not valid." >&2; exit 1 ;;
    esac
    if systemctl cat mk8email.service 2>/dev/null \
        | grep -Eq '^ExecStart=.* --serve$'; then
        rollback_supported=true
    elif systemctl is-active --quiet mk8email-gateway.service mk8email-worker.service; then
        rollback_supported=true
    elif systemctl is-active --quiet mk8email-gateway.service mk8email-wake.service mk8email-worker.path mk8email-worker-maintenance.timer; then
        rollback_supported=true
    fi
    timeout 180s apt-get update
    /usr/local/sbin/verify-host-prerequisites
    backup_path=`$(/usr/local/sbin/mk8-backup)
    case "`$backup_path" in
        /var/backups/mk8/20??????T??????Z) ;;
        *) printf '%s\n' "The deployment backup path is not valid." >&2; exit 1 ;;
    esac
    (cd "`$backup_path" && sha256sum --check --status SHA256SUMS) \
        || { printf '%s\n' "The deployment backup checksum is not valid." >&2; exit 1; }
elif [ -x /usr/local/sbin/verify-host-prerequisites ] \
    && [ -d /usr/local/share/mk8email/prerequisites ]; then
    timeout 180s apt-get update
    sh "`$remote_root/assets/deploy/scripts/verify-host-prerequisites" \
        "`$remote_root/assets/deploy/prerequisites"
fi

apt_source_backup="`$remote_root/apt-source-backup"
install -d -o root -g root -m 0700 "`$apt_source_backup"
for managed_source in \
    /etc/apt/sources.list \
    /etc/apt/sources.list.d/debian.sources; do
    if [ -L "`$managed_source" ] \
        || { [ -e "`$managed_source" ] && [ ! -f "`$managed_source" ]; }; then
        printf '%s\n' "A managed APT source path is unsafe." >&2
        exit 1
    fi
done
if [ -f /etc/apt/sources.list ]; then
    cp --archive /etc/apt/sources.list "`$apt_source_backup/sources.list"
fi
if [ -f /etc/apt/sources.list.d/debian.sources ]; then
    cp --archive /etc/apt/sources.list.d/debian.sources \
        "`$apt_source_backup/debian.sources"
fi
apt_sources_changing=true
sh "`$remote_root/assets/deploy/scripts/configure-apt-sources" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"
timeout 180s apt-get update
sh "`$remote_root/assets/deploy/scripts/verify-host-prerequisites" \
    "`$remote_root/assets/deploy/prerequisites"
sh "`$remote_root/assets/deploy/tests/apt_sources_smoke" \
    "`$remote_root/assets/deploy/scripts/configure-apt-sources" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"

if [ -n "`$backup_path" ] && [ -s /etc/mk8email/secrets/backup-age-recipient ]; then
    backup_name=`${backup_path##*/}
    encrypted_name="`$backup_name.tar.gz.age"
    if [ ! -s "/var/backups/mk8-export/`$encrypted_name" ] \
        || [ ! -s "/var/backups/mk8-export/`$encrypted_name.sha256" ]; then
        bash "`$remote_root/assets/deploy/scripts/mk8-export-backup" "`$backup_name" >/dev/null
    fi
    (cd /var/backups/mk8-export && sha256sum --check --status "`$encrypted_name.sha256")
fi

changes_started=true
sh "`$remote_root/assets/deploy/scripts/install-native-release" \
    "`$remote_root/mk8email-release.tar.gz" '$releaseId' \
    "`$source_commit" "`$release_digest" "`$asset_digest"
sh "`$remote_root/assets/deploy/scripts/install-mail-stack" "`$remote_root/assets"
if [ -n "`$previous_release" ]; then
    /usr/local/sbin/prune-native-releases "`$previous_release"
else
    /usr/local/sbin/prune-native-releases
fi
/usr/local/lib/mk8email/tests/release_retention_smoke
printf '%s\n' "Running the native release provenance smoke test." >&2
/usr/local/lib/mk8email/tests/native_release_smoke
if [ "`$activate_requested" = true ] || [ "`$was_active" = true ]; then
    /usr/local/sbin/activate-mail-stack
    printf '%s\n' "Running the management command smoke test." >&2
    /usr/local/lib/mk8email/tests/management_cli_smoke
    printf '%s\n' "Running the certificate deployment smoke test." >&2
    /usr/local/lib/mk8email/tests/certificate_deploy_smoke
    printf '%s\n' "Running the multi-domain smoke test." >&2
    /usr/local/lib/mk8email/tests/multi_domain_smoke
fi
apt_sources_changing=false
"@
    [IO.File]::WriteAllText($remoteScript, $remoteScriptText, [Text.UTF8Encoding]::new($false))

    Invoke-BoundedProcess ssh.exe ($sshOptions + @($Target, "install -d -o root -g root -m 0700 '$remoteRoot'")) 60 $repositoryRoot
    Invoke-BoundedProcess scp.exe ($sshOptions + @($assetArchive, "$Target`:$remoteRoot/mk8email-assets.tar.gz")) 180 $repositoryRoot
    Invoke-BoundedProcess scp.exe ($sshOptions + @($remoteScript, "$Target`:$remoteRoot/remote-install.sh")) 60 $repositoryRoot
    Invoke-BoundedProcess ssh.exe ($sshOptions + @($Target, "timeout 900s sh '$remoteRoot/remote-install.sh'")) 930 $repositoryRoot

    Write-Host "Deployed server-built commit $commit as release $releaseId."
}
finally {
    $resolvedTaskRoot = [IO.Path]::GetFullPath($taskRoot).TrimEnd('\') + '\'
    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    if ($resolvedRunRoot.StartsWith($resolvedTaskRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedRunRoot)) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
