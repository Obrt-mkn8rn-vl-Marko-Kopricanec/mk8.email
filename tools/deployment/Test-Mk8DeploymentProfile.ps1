#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TaskRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool]$Condition,
        [Parameter(Mandatory)] [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)] [scriptblock]$Action,
        [Parameter(Mandatory)] [string]$Pattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "The failure did not match '$Pattern': $($_.Exception.Message)"
        }
        return
    }
    throw "The operation did not fail as expected: $Pattern"
}

function Write-TestProfile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [Collections.IDictionary]$Values
    )

    $json = $Values | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$modulePath = Join-Path $PSScriptRoot 'Mk8DeploymentProfile.psm1'
Import-Module $modulePath -Force

$taskPath = [IO.Path]::GetFullPath($TaskRoot)
$runPath = Join-Path $taskPath ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runPath) | Out-Null

try {
    $testRepository = Join-Path $runPath 'repository'
    $testDeploy = Join-Path $testRepository 'deploy'
    $testSecrets = Join-Path $testDeploy 'secrets'
    [IO.Directory]::CreateDirectory($testDeploy) | Out-Null
    foreach ($sourceItem in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'deploy') -Force) {
        if ($sourceItem.Name -cne 'secrets') {
            Copy-Item -LiteralPath $sourceItem.FullName -Destination $testDeploy -Recurse -Force
        }
    }
    [IO.Directory]::CreateDirectory($testSecrets) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'compose.yaml') -Destination $testRepository

    $keyPath = Join-Path $runPath 'test-key'
    $knownHostsPath = Join-Path $runPath 'known-hosts'
    $backupPath = Join-Path $runPath 'backups'
    [IO.File]::WriteAllText($keyPath, 'test-key', [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($knownHostsPath, 'test-host', [Text.Encoding]::ASCII)

    $validProfile = [ordered]@{
        Version = 2
        ServerIPv4 = '10.44.8.25'
        LanCidr = '10.44.8.0/24'
        TrustedAdminIPv4 = '10.44.8.1'
        PublicIPv4 = '8.8.8.8'
        SshKeyPath = $keyPath
        KnownHostsPath = $knownHostsPath
        BackupDestination = $backupPath
        PrimaryDomain = 'example.test'
        MailHostname = 'mx.example.test'
        AdminHostname = 'admin.example.test'
        AutoconfigHostname = 'autoconfig.example.test'
        MtaStsHostname = 'mta-sts.example.test'
        ContainerCidr = '10.55.0.0/24'
        DkimSelector = 'fixture1'
        DmarcReportLocalPart = 'reports'
        TlsReportLocalPart = 'tls-reports'
        AdministratorLocalPart = 'administrator'
        PrimaryLocalPart = 'primary'
        CompanyId = 'fixture-company'
        MtaStsPolicyId = 'fixture-policy-1'
        CertificateAuthority = 'ca.example.test'
        WanInterfaceList = 'fixture-wan'
        DatabaseHost = 'db.example.test'
        ObjectStorageContainer = 'fixture-objects'
    }
    $profilePath = Join-Path $testSecrets 'production-profile.json'
    Write-TestProfile -Path $profilePath -Values $validProfile

    $profile = Import-Mk8DeploymentProfile `
        -Path $profilePath `
        -RepositoryRoot $testRepository `
        -RequireConnectionFiles
    Assert-True ($profile.ServerIPv4 -ceq $validProfile.ServerIPv4) 'The server address changed during import.'
    Assert-True ($profile.LanCidr -ceq $validProfile.LanCidr) 'The LAN CIDR changed during import.'

    $renderedRoot = Join-Path $runPath 'rendered'
    New-Mk8RenderedDeployAssets `
        -ProfilePath $profilePath `
        -RepositoryRoot $testRepository `
        -Destination $renderedRoot | Out-Null
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $renderedRoot 'deploy/secrets'))) `
        'The rendered assets copied the private profile directory.'

    $renderedFiles = Get-ChildItem -LiteralPath (Join-Path $renderedRoot 'deploy') -File -Recurse -Force
    $unresolved = $renderedFiles | Select-String -Pattern '@@MK8_[A-Z0-9_]+@@' | Select-Object -First 1
    Assert-True ($null -eq $unresolved) 'The rendered assets contain an unresolved token.'
    $renderedNginx = Get-Content -LiteralPath (Join-Path $renderedRoot 'deploy/nginx/mk8-admin.conf') -Raw
    Assert-True ($renderedNginx.Contains($validProfile.ServerIPv4, [StringComparison]::Ordinal)) `
        'The rendered nginx configuration does not contain the server address.'
    Assert-True ($renderedNginx.Contains($validProfile.LanCidr, [StringComparison]::Ordinal)) `
        'The rendered nginx configuration does not contain the LAN CIDR.'
    $renderedMailConfig = Get-Content `
        -LiteralPath (Join-Path $renderedRoot 'deploy/native/mk8email.config.json') -Raw
    Assert-True ($renderedMailConfig.Contains(
        '"Hostname": "mx.example.test"',
        [StringComparison]::Ordinal)) `
        'The rendered native mail configuration has the wrong mail hostname.'
    $renderedDns = Get-Content `
        -LiteralPath (Join-Path $renderedRoot 'deploy/dns/primary-domain.zone') -Raw
    Assert-True ($renderedDns.Contains(
        'IN MX 10 mx.example.test.',
        [StringComparison]::Ordinal)) `
        'The rendered MX record has the wrong mail hostname.'
    Assert-True ($renderedDns.Contains(
        '_caldavs._tcp                   IN SRV 0 1 443 mx.example.test.',
        [StringComparison]::Ordinal)) `
        'The rendered DNS records omit CalDAV discovery.'
    Assert-True ($renderedDns.Contains(
        '_carddavs._tcp                  IN SRV 0 1 443 mx.example.test.',
        [StringComparison]::Ordinal)) `
        'The rendered DNS records omit CardDAV discovery.'
    foreach ($routerFile in @('mk8-web-preflight.rsc', 'mk8-public-services.rsc')) {
        $renderedRouter = Get-Content `
            -LiteralPath (Join-Path $renderedRoot "deploy/routeros/$routerFile") -Raw
        Assert-True ($renderedRouter.Contains(
            "dst-address=$($validProfile.PublicIPv4)",
            [StringComparison]::Ordinal)) `
            "$routerFile does not bind NAT to the public address."
        Assert-True (-not $renderedRouter.Contains(
            'dst-address-type=local',
            [StringComparison]::Ordinal)) `
            "$routerFile can match an unrelated WAN address."
    }
    $renderedAutoconfig = Get-Content `
        -LiteralPath (Join-Path $renderedRoot 'deploy/nginx/www/autoconfig/mail/config-v1.1.xml') -Raw
    Assert-True ($renderedAutoconfig.Contains(
        '<hostname>mx.example.test</hostname>',
        [StringComparison]::Ordinal)) `
        'The rendered client configuration has the wrong mail hostname.'
    Assert-True ($renderedAutoconfig.Contains(
        '<url>https://mx.example.test/.well-known/jmap</url>',
        [StringComparison]::Ordinal)) `
        'The rendered client configuration omits the preferred JMAP endpoint.'
    Assert-True ($renderedAutoconfig.Contains(
        '<url>https://mx.example.test/.well-known/caldav</url>',
        [StringComparison]::Ordinal)) `
        'The rendered client configuration omits CalDAV.'
    Assert-True ($renderedAutoconfig.Contains(
        '<url>https://mx.example.test/.well-known/carddav</url>',
        [StringComparison]::Ordinal)) `
        'The rendered client configuration omits CardDAV.'
    $sourceNginx = Get-Content -LiteralPath (Join-Path $testDeploy 'nginx/mk8-admin.conf') -Raw
    Assert-True ($sourceNginx.Contains('@@MK8_SERVER_IPV4@@', [StringComparison]::Ordinal)) `
        'Rendering changed the deployment source.'

    # All addresses below are numeric classification/rendering fixtures. No sockets,
    # DNS or deployment scripts are invoked, and PublicIPv4 is never an allocation.
    $renderedCompose = Get-Content -LiteralPath (Join-Path $renderedRoot 'compose.yaml') -Raw
    Assert-True ($renderedCompose.Contains($validProfile.ContainerCidr, [StringComparison]::Ordinal)) 'Compose subnet was not rendered.'
    Assert-True ($renderedDns.Contains('mailto:reports@example.test', [StringComparison]::Ordinal)) 'DMARC report input was lost.'
    Assert-True ($renderedDns.Contains('ca.example.test', [StringComparison]::Ordinal)) 'CAA input was lost.'
    Assert-True (-not $renderedDns.Contains('p=MIIB', [StringComparison]::Ordinal)) 'A public deployment key was shipped.'
    $second = [ordered]@{} + $validProfile
    $second.PrimaryDomain = 'example.invalid'
    $second.MailHostname = 'mail.example.invalid'
    $second.AdminHostname = 'private.example.invalid'
    $second.AutoconfigHostname = 'autoconfig.example.invalid'
    $second.MtaStsHostname = 'mta-sts.example.invalid'
    $second.DatabaseHost = 'db.example.invalid'
    Write-TestProfile -Path $profilePath -Values $second
    New-Mk8RenderedDeployAssets -ProfilePath $profilePath -RepositoryRoot $testRepository -Destination (Join-Path $runPath 'second') | Out-Null
    $secondConfig = Get-Content -LiteralPath (Join-Path $runPath 'second/deploy/native/mk8email.config.json') -Raw
    Assert-True ($secondConfig.Contains('https://mail.example.invalid', [StringComparison]::Ordinal)) 'A second independent domain was not rendered.'
    Assert-True (-not $secondConfig.Contains('example.test', [StringComparison]::Ordinal)) 'The first profile leaked into the second render.'
    foreach ($case in @(
        @('PrimaryDomain', 'EXAMPLE.test', 'canonical'),
        @('PrimaryDomain', '127.0.0.1', 'canonical'),
        @('MailHostname', 'mail.other.test', 'beneath'),
        @('AdminHostname', 'mx.example.test', 'distinct'),
        @('PrimaryDomain', 'x.test;touch', 'canonical'),
        @('DmarcReportLocalPart', 'x";touch', 'template-safe'),
        @('DkimSelector', 'x._domainkey', 'template-safe'),
        @('WanInterfaceList', 'wan;command', 'template-safe'),
        @('ContainerCidr', '10.44.8.0/24', 'overlap'),
        @('ContainerCidr', '10.0.0.0/08', 'canonical'),
        @('ContainerCidr', '172.0.0.0/8', 'private'),
        @('CertificateAuthority', 'https://ca.example.test', 'canonical'),
        @('DatabaseHost', 'host;Password=secret', 'canonical'),
        @('ObjectStorageContainer', 'INVALID', 'container name'),
        @('ObjectStorageContainer', 'invalid--name', 'container name'),
        @('PrimaryDomain', "example.test`n", 'canonical'),
        @('WanInterfaceList', "fixture`n", 'template-safe'),
        @('CompanyId', "fixture`n", 'template-safe'),
        @('DkimSelector', "fixture1`n", 'template-safe'),
        @('MtaStsPolicyId', "fixture1`n", 'template-safe'),
        @('DmarcReportLocalPart', "reports`n", 'template-safe'),
        @('ContainerCidr', "10.55.0.0/24`n", 'canonical'),
        @('ObjectStorageContainer', "fixture-objects`n", 'container name')
    )) {
        $invalid = [ordered]@{} + $validProfile
        $invalid[$case[0]] = $case[1]
        Write-TestProfile -Path $profilePath -Values $invalid
        Assert-Throws { Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository } $case[2]
    }
    $missingDomain = [ordered]@{} + $validProfile
    $missingDomain.Remove('PrimaryDomain')
    Write-TestProfile -Path $profilePath -Values $missingDomain
    Assert-Throws { Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository } 'missing a property'
    $duplicate = ($validProfile | ConvertTo-Json) -replace '"Version": 2', '"Version": 2, "Version": 2'
    [IO.File]::WriteAllText($profilePath, $duplicate, [Text.UTF8Encoding]::new($false))
    Assert-Throws { Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository } 'duplicate'

    $invalidCidr = [ordered]@{} + $validProfile
    $invalidCidr.LanCidr = '10.44.8.1/24'
    Write-TestProfile -Path $profilePath -Values $invalidCidr
    Assert-Throws {
        Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository
    } 'network address'

    $publicLan = [ordered]@{} + $validProfile
    $publicLan.LanCidr = '8.8.8.0/24'
    $publicLan.ServerIPv4 = '8.8.8.25'
    $publicLan.TrustedAdminIPv4 = '8.8.8.1'
    Write-TestProfile -Path $profilePath -Values $publicLan
    Assert-Throws {
        Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository
    } 'private IPv4 range'

    $outsideServer = [ordered]@{} + $validProfile
    $outsideServer.ServerIPv4 = '10.44.9.25'
    Write-TestProfile -Path $profilePath -Values $outsideServer
    Assert-Throws {
        Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository
    } 'inside LanCidr'

    $missingKey = [ordered]@{} + $validProfile
    $missingKey.SshKeyPath = Join-Path $runPath 'missing-key'
    Write-TestProfile -Path $profilePath -Values $missingKey
    Assert-Throws {
        Import-Mk8DeploymentProfile `
            -Path $profilePath `
            -RepositoryRoot $testRepository `
            -RequireConnectionFiles
    } 'SshKeyPath does not exist'

    $unknownProperty = [ordered]@{} + $validProfile
    $unknownProperty.Add('Unexpected', 'value')
    Write-TestProfile -Path $profilePath -Values $unknownProperty
    Assert-Throws {
        Import-Mk8DeploymentProfile -Path $profilePath -RepositoryRoot $testRepository
    } 'unknown property'

    Write-TestProfile -Path $profilePath -Values $validProfile
    $outsideProfile = Join-Path $runPath 'outside-profile.json'
    Write-TestProfile -Path $outsideProfile -Values $validProfile
    Assert-Throws {
        Import-Mk8DeploymentProfile -Path $outsideProfile -RepositoryRoot $testRepository
    } 'directly under deploy/secrets'

    $existingDestination = Join-Path $runPath 'existing-render'
    [IO.Directory]::CreateDirectory($existingDestination) | Out-Null
    Assert-Throws {
        New-Mk8RenderedDeployAssets `
            -ProfilePath $profilePath `
            -RepositoryRoot $testRepository `
            -Destination $existingDestination
    } 'already exists'

    $unknownTokenPath = Join-Path $testDeploy 'unknown-token.txt'
    [IO.File]::WriteAllText($unknownTokenPath, '@@MK8_UNKNOWN@@', [Text.Encoding]::ASCII)
    Assert-Throws {
        New-Mk8RenderedDeployAssets `
            -ProfilePath $profilePath `
            -RepositoryRoot $testRepository `
            -Destination (Join-Path $runPath 'unknown-render')
    } 'unknown token'

    Write-Host 'Deployment profile validation and rendering tests passed.'
}
finally {
    $resolvedTaskPath = [IO.Path]::GetFullPath($taskPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedRunPath = [IO.Path]::GetFullPath($runPath)
    if ($resolvedRunPath.StartsWith($resolvedTaskPath, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $resolvedRunPath)) {
        Remove-Item -LiteralPath $resolvedRunPath -Recurse -Force
    }
}
