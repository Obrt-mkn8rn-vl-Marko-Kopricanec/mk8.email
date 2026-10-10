Set-StrictMode -Version Latest

$script:Mk8ProfilePropertyNames = @(
    'Version'
    'ServerIPv4'
    'LanCidr'
    'TrustedAdminIPv4'
    'PublicIPv4'
    'SshKeyPath'
    'KnownHostsPath'
    'BackupDestination'
    'PrimaryDomain'
    'MailHostname'
    'AdminHostname'
    'AutoconfigHostname'
    'MtaStsHostname'
    'ContainerCidr'
    'DkimSelector'
    'DmarcReportLocalPart'
    'TlsReportLocalPart'
    'AdministratorLocalPart'
    'PrimaryLocalPart'
    'CompanyId'
    'MtaStsPolicyId'
    'CertificateAuthority'
    'WanInterfaceList'
    'DatabaseHost'
    'ObjectStorageContainer'
)

$script:Mk8TokenByProperty = [ordered]@{
    '@@MK8_SERVER_IPV4@@' = 'ServerIPv4'
    '@@MK8_LAN_CIDR@@' = 'LanCidr'
    '@@MK8_TRUSTED_ADMIN_IPV4@@' = 'TrustedAdminIPv4'
    '@@MK8_PUBLIC_IPV4@@' = 'PublicIPv4'
    '@@MK8_PRIMARY_DOMAIN@@' = 'PrimaryDomain'
    '@@MK8_MAIL_HOST@@' = 'MailHostname'
    '@@MK8_ADMIN_HOST@@' = 'AdminHostname'
    '@@MK8_MAIL_RELATIVE_NAME@@' = 'MailRelativeName'
    '@@MK8_AUTOCONFIG_RELATIVE_NAME@@' = 'AutoconfigRelativeName'
    '@@MK8_MTA_STS_RELATIVE_NAME@@' = 'MtaStsRelativeName'
    '@@MK8_CONTAINER_CIDR@@' = 'ContainerCidr'
    '@@MK8_DKIM_SELECTOR@@' = 'DkimSelector'
    '@@MK8_DMARC_LOCAL_PART@@' = 'DmarcReportLocalPart'
    '@@MK8_TLS_REPORT_LOCAL_PART@@' = 'TlsReportLocalPart'
    '@@MK8_ADMIN_LOCAL_PART@@' = 'AdministratorLocalPart'
    '@@MK8_PRIMARY_LOCAL_PART@@' = 'PrimaryLocalPart'
    '@@MK8_COMPANY_ID@@' = 'CompanyId'
    '@@MK8_MTA_STS_POLICY_ID@@' = 'MtaStsPolicyId'
    '@@MK8_CERTIFICATE_AUTHORITY@@' = 'CertificateAuthority'
    '@@MK8_WAN_INTERFACE_LIST@@' = 'WanInterfaceList'
    '@@MK8_DATABASE_HOST@@' = 'DatabaseHost'
    '@@MK8_OBJECT_CONTAINER@@' = 'ObjectStorageContainer'
}

# Only canonical ASCII labels are substituted into shell/JSON/XML/DNS assets.
# No operator input may introduce escaping, metacharacters or extra statements.
function Assert-Mk8DnsName {
    param([Parameter(Mandatory)] [string]$Value, [Parameter(Mandatory)] [string]$Name)
    $address = $null
    if ($Value.Length -gt 253 -or $Value -cne $Value.ToLowerInvariant() `
        -or [Net.IPAddress]::TryParse($Value, [ref]$address) `
        -or -not [regex]::IsMatch($Value, '\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z')) {
        throw "$Name must be a canonical lower-case ASCII DNS name, not an IP address."
    }
}

function Get-Mk8PrivateCidr {
    param([Parameter(Mandatory)] [string]$Value, [Parameter(Mandatory)] [string]$Name)
    $match = [regex]::Match($Value, '\A(?<address>[^/]+)/(?<prefix>[0-9]{1,2})\z')
    if (-not $match.Success) { throw "$Name must be a canonical IPv4 CIDR." }
    $address = Get-Mk8IPv4Details -Value $match.Groups['address'].Value -Name $Name
    $prefix = [int]$match.Groups['prefix'].Value
    if ($prefix -lt 8 -or $prefix -gt 30 -or [string]$prefix -cne $match.Groups['prefix'].Value) {
        throw "$Name must have a canonical prefix length from 8 through 30."
    }
    if (-not (Test-Mk8PrivateIPv4 -Bytes $address.Bytes)) { throw "$Name must use a private IPv4 range." }
    $blockSize = [uint64][Math]::Pow(2, 32 - $prefix)
    if (($address.Number % $blockSize) -ne 0) { throw "$Name must start at its network address." }
    $last = $address.Number + $blockSize - 1
    $lastBytes = [Net.IPAddress]::Parse(($last -shr 24).ToString() + '.' + (($last -shr 16) -band 255) + '.' + (($last -shr 8) -band 255) + '.' + ($last -band 255)).GetAddressBytes()
    if (-not (Test-Mk8PrivateIPv4 -Bytes $lastBytes)) { throw "$Name must lie entirely inside a private IPv4 range." }
    return [pscustomobject]@{ Number = $address.Number; Last = $last }
}

function Test-Mk8PathAtOrWithin {
    param(
        [Parameter(Mandatory)] [string]$Candidate,
        [Parameter(Mandatory)] [string]$Root
    )

    $candidatePath = [IO.Path]::GetFullPath($Candidate)
    $rootPath = [IO.Path]::GetFullPath($Root)
    $relativePath = [IO.Path]::GetRelativePath($rootPath, $candidatePath)
    if ($relativePath -eq '.') {
        return $true
    }

    $parentPrefix = "..$([IO.Path]::DirectorySeparatorChar)"
    $alternateParentPrefix = "..$([IO.Path]::AltDirectorySeparatorChar)"
    return $relativePath -ne '..' `
        -and -not $relativePath.StartsWith($parentPrefix, [StringComparison]::Ordinal) `
        -and -not $relativePath.StartsWith($alternateParentPrefix, [StringComparison]::Ordinal) `
        -and -not [IO.Path]::IsPathRooted($relativePath)
}

function Get-Mk8IPv4Details {
    param(
        [Parameter(Mandatory)] [string]$Value,
        [Parameter(Mandatory)] [string]$Name
    )

    $address = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref]$address) `
        -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork `
        -or $address.ToString() -cne $Value) {
        throw "$Name must be a canonical IPv4 address."
    }

    $bytes = $address.GetAddressBytes()
    $number = ([uint64]$bytes[0] * 16777216) `
        + ([uint64]$bytes[1] * 65536) `
        + ([uint64]$bytes[2] * 256) `
        + [uint64]$bytes[3]
    return [pscustomobject]@{
        Address = $address
        Bytes = $bytes
        Number = $number
    }
}

function Test-Mk8PrivateIPv4 {
    param([Parameter(Mandatory)] [byte[]]$Bytes)

    return $Bytes[0] -eq 10 `
        -or ($Bytes[0] -eq 172 -and $Bytes[1] -ge 16 -and $Bytes[1] -le 31) `
        -or ($Bytes[0] -eq 192 -and $Bytes[1] -eq 168)
}

function Test-Mk8NumberInCidr {
    param(
        [Parameter(Mandatory)] [uint64]$Number,
        [Parameter(Mandatory)] [uint64]$Network,
        [Parameter(Mandatory)] [int]$PrefixLength
    )

    $blockSize = [uint64][Math]::Pow(2, 32 - $PrefixLength)
    return ($Number - ($Number % $blockSize)) -eq $Network
}

function Test-Mk8PublicIPv4 {
    param([Parameter(Mandatory)] [pscustomobject]$Details)

    if (Test-Mk8PrivateIPv4 -Bytes $Details.Bytes) {
        return $false
    }

    $blockedRanges = @(
        @('0.0.0.0', 8),
        @('100.64.0.0', 10),
        @('127.0.0.0', 8),
        @('169.254.0.0', 16),
        @('192.0.0.0', 24),
        @('192.0.2.0', 24),
        @('192.88.99.0', 24),
        @('198.18.0.0', 15),
        @('198.51.100.0', 24),
        @('203.0.113.0', 24),
        @('224.0.0.0', 4),
        @('240.0.0.0', 4)
    )
    foreach ($range in $blockedRanges) {
        $network = (Get-Mk8IPv4Details -Value $range[0] -Name 'Blocked range').Number
        if (Test-Mk8NumberInCidr -Number $Details.Number -Network $network -PrefixLength $range[1]) {
            return $false
        }
    }

    return $true
}

function Resolve-Mk8DeploymentProfilePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$RepositoryRoot
    )

    $repositoryPath = [IO.Path]::GetFullPath($RepositoryRoot)
    if (-not (Test-Path -LiteralPath $repositoryPath -PathType Container)) {
        throw 'The repository root does not exist.'
    }

    $profilePath = [IO.Path]::GetFullPath($Path)
    $secretsPath = [IO.Path]::GetFullPath((Join-Path $repositoryPath 'deploy/secrets'))
    $comparison = if ([OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if (-not [IO.Path]::GetDirectoryName($profilePath).Equals($secretsPath, $comparison) `
        -or [IO.Path]::GetExtension($profilePath) -cne '.json') {
        throw 'The deployment profile must be a JSON file directly under deploy/secrets.'
    }
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
        throw 'The deployment profile does not exist.'
    }

    foreach ($safePath in @($secretsPath, $profilePath)) {
        $item = Get-Item -LiteralPath $safePath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The deployment profile path cannot contain a reparse point.'
        }
    }

    return $profilePath
}

function Import-Mk8DeploymentProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [switch]$RequireConnectionFiles
    )

    $repositoryPath = [IO.Path]::GetFullPath($RepositoryRoot)
    $profilePath = Resolve-Mk8DeploymentProfilePath -Path $Path -RepositoryRoot $repositoryPath
    $rawProfile = [IO.File]::ReadAllText($profilePath, [Text.Encoding]::UTF8)
    try {
        $document = [Text.Json.JsonDocument]::Parse($rawProfile)
    }
    catch {
        throw 'The deployment profile is not valid JSON.'
    }

    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'The deployment profile must contain one JSON object.'
        }

        $values = [ordered]@{}
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if (-not $seen.Add($property.Name)) {
                throw "The deployment profile contains a duplicate property: $($property.Name)."
            }
            if (-not ($script:Mk8ProfilePropertyNames -ccontains $property.Name)) {
                throw "The deployment profile contains an unknown property: $($property.Name)."
            }

            if ($property.Name -ceq 'Version') {
                $version = 0
                if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::Number `
                    -or -not $property.Value.TryGetInt32([ref]$version)) {
                    throw 'Version must be an integer.'
                }
                $values[$property.Name] = $version
            }
            else {
                if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw "$($property.Name) must be a string."
                }
                $values[$property.Name] = $property.Value.GetString()
            }
        }

        foreach ($name in $script:Mk8ProfilePropertyNames) {
            if (-not $seen.Contains($name)) {
                throw "The deployment profile is missing a property: $name."
            }
        }
    }
    finally {
        $document.Dispose()
    }

    if ($values.Version -ne 2) {
        throw 'The deployment profile version is not supported.'
    }
    foreach ($name in $script:Mk8ProfilePropertyNames | Where-Object { $_ -cne 'Version' }) {
        if ([string]::IsNullOrWhiteSpace($values[$name])) {
            throw "$name cannot be empty."
        }
    }

    foreach ($name in @('PrimaryDomain', 'MailHostname', 'AdminHostname', 'AutoconfigHostname', 'MtaStsHostname', 'CertificateAuthority')) {
        Assert-Mk8DnsName -Value $values[$name] -Name $name
    }
    $hosts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in @('MailHostname', 'AdminHostname', 'AutoconfigHostname', 'MtaStsHostname')) {
        if (-not $hosts.Add($values[$name])) { throw 'Service hostnames must be distinct.' }
    }
    foreach ($name in @('Mail', 'Autoconfig', 'MtaSts')) {
        $suffix = '.' + $values.PrimaryDomain
        $hostName = $values[$name + 'Hostname']
        if (-not $hostName.EndsWith($suffix, [StringComparison]::Ordinal)) {
            throw "$name`Hostname must be beneath PrimaryDomain for the DNS templates."
        }
        $values[$name + 'RelativeName'] = $hostName.Substring(0, $hostName.Length - $suffix.Length)
    }
    # The existing per-domain discovery renderer uses these protocol/convention
    # labels. Reject inconsistent profile inputs rather than advertising a route
    # that its unchanged certificate/health contracts do not handle.
    if ($values.AutoconfigHostname -cne ('autoconfig.' + $values.PrimaryDomain) `
        -or $values.MtaStsHostname -cne ('mta-sts.' + $values.PrimaryDomain)) {
        throw 'Discovery hostnames must match the declared per-domain discovery labels.'
    }
    foreach ($name in @('DmarcReportLocalPart', 'TlsReportLocalPart', 'AdministratorLocalPart', 'PrimaryLocalPart')) {
        if (-not [regex]::IsMatch($values[$name], '\A[a-z0-9]+(?:[._+-][a-z0-9]+)*\z') `
            -or $values[$name].Length -gt 64 `
            -or ($values[$name].Length + 1 + $values.PrimaryDomain.Length) -gt 254) {
            throw "$name must be a bounded canonical template-safe mail local part."
        }
    }
    if ($values.AdministratorLocalPart -ceq $values.PrimaryLocalPart) { throw 'Administrator and primary local parts must differ.' }
    foreach ($name in @('DkimSelector', 'CompanyId', 'MtaStsPolicyId', 'WanInterfaceList')) {
        if (-not [regex]::IsMatch($values[$name], '\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}\z')) {
            throw "$name must be a bounded template-safe identifier."
        }
    }
    if (-not [regex]::IsMatch($values.DkimSelector, '\A[a-z0-9]{1,63}\z')) {
        throw 'DkimSelector must be a canonical lower-case alphanumeric DNS label.'
    }
    $container = Get-Mk8PrivateCidr -Value $values.ContainerCidr -Name 'ContainerCidr'
    $lan = Get-Mk8PrivateCidr -Value $values.LanCidr -Name 'LanCidr'
    if ($container.Number -le $lan.Last -and $lan.Number -le $container.Last) {
        throw 'ContainerCidr must not overlap LanCidr.'
    }
    $databaseAddress = $null
    if ([Net.IPAddress]::TryParse($values.DatabaseHost, [ref]$databaseAddress)) {
        if ($databaseAddress.ToString() -cne $values.DatabaseHost) { throw 'DatabaseHost must be canonical.' }
    }
    else {
        Assert-Mk8DnsName -Value $values.DatabaseHost -Name 'DatabaseHost'
    }
    if (-not [regex]::IsMatch($values.ObjectStorageContainer, '\A[a-z0-9][a-z0-9-]{1,61}[a-z0-9]\z') `
        -or $values.ObjectStorageContainer.Contains('--', [StringComparison]::Ordinal)) {
        throw 'ObjectStorageContainer must be a valid lower-case Azure Blob container name.'
    }

    $server = Get-Mk8IPv4Details -Value $values.ServerIPv4 -Name 'ServerIPv4'
    $administrator = Get-Mk8IPv4Details -Value $values.TrustedAdminIPv4 -Name 'TrustedAdminIPv4'
    $public = Get-Mk8IPv4Details -Value $values.PublicIPv4 -Name 'PublicIPv4'

    $cidrMatch = [regex]::Match($values.LanCidr, '\A(?<address>[^/]+)/(?<prefix>[0-9]{1,2})\z')
    if (-not $cidrMatch.Success) {
        throw 'LanCidr must be a canonical IPv4 CIDR.'
    }
    $lanAddress = Get-Mk8IPv4Details -Value $cidrMatch.Groups['address'].Value -Name 'LanCidr'
    $prefixLength = [int]$cidrMatch.Groups['prefix'].Value
    if ($prefixLength -lt 8 -or $prefixLength -gt 30) {
        throw 'LanCidr must have a prefix length from 8 through 30.'
    }
    if (-not (Test-Mk8PrivateIPv4 -Bytes $lanAddress.Bytes)) {
        throw 'LanCidr must use a private IPv4 range.'
    }
    $blockSize = [uint64][Math]::Pow(2, 32 - $prefixLength)
    if (($lanAddress.Number % $blockSize) -ne 0) {
        throw 'LanCidr must start at its network address.'
    }
    foreach ($entry in @(
        @{ Name = 'ServerIPv4'; Details = $server },
        @{ Name = 'TrustedAdminIPv4'; Details = $administrator }
    )) {
        if (-not (Test-Mk8PrivateIPv4 -Bytes $entry.Details.Bytes) `
            -or -not (Test-Mk8NumberInCidr -Number $entry.Details.Number -Network $lanAddress.Number -PrefixLength $prefixLength)) {
            throw "$($entry.Name) must be inside LanCidr."
        }
        if ($entry.Details.Number -eq $lanAddress.Number `
            -or $entry.Details.Number -eq ($lanAddress.Number + $blockSize - 1)) {
            throw "$($entry.Name) cannot be a network or broadcast address."
        }
    }
    if ($server.Number -eq $administrator.Number) {
        throw 'ServerIPv4 and TrustedAdminIPv4 must be different.'
    }
    if (-not (Test-Mk8PublicIPv4 -Details $public)) {
        throw 'PublicIPv4 must be a globally routable IPv4 address.'
    }

    foreach ($name in @('SshKeyPath', 'KnownHostsPath', 'BackupDestination')) {
        if (-not [IO.Path]::IsPathFullyQualified($values[$name])) {
            throw "$name must be a fully qualified path."
        }
        $values[$name] = [IO.Path]::GetFullPath($values[$name])
        if (Test-Mk8PathAtOrWithin -Candidate $values[$name] -Root $repositoryPath) {
            throw "$name must be outside the repository."
        }
    }
    if ($values.SshKeyPath -ceq $values.KnownHostsPath) {
        throw 'SshKeyPath and KnownHostsPath must be different.'
    }
    if ((Test-Path -LiteralPath $values.BackupDestination) `
        -and -not (Test-Path -LiteralPath $values.BackupDestination -PathType Container)) {
        throw 'BackupDestination must be a directory.'
    }
    if ($RequireConnectionFiles) {
        foreach ($name in @('SshKeyPath', 'KnownHostsPath')) {
            if (-not (Test-Path -LiteralPath $values[$name] -PathType Leaf)) {
                throw "$name does not exist."
            }
            $item = Get-Item -LiteralPath $values[$name] -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$name cannot be a reparse point."
            }
        }
    }

    return [pscustomobject]$values
}

function New-Mk8RenderedDeployAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ProfilePath,
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [Parameter(Mandatory)] [string]$Destination
    )

    $repositoryPath = [IO.Path]::GetFullPath($RepositoryRoot)
    $sourcePath = Join-Path $repositoryPath 'deploy'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
        throw 'The deployment source directory does not exist.'
    }

    $destinationPath = [IO.Path]::GetFullPath($Destination)
    if (Test-Mk8PathAtOrWithin -Candidate $destinationPath -Root $repositoryPath) {
        throw 'The rendered deployment destination must be outside the repository.'
    }
    if (Test-Path -LiteralPath $destinationPath) {
        throw 'The rendered deployment destination already exists.'
    }

    $profile = Import-Mk8DeploymentProfile -Path $ProfilePath -RepositoryRoot $repositoryPath
    $created = $false
    try {
        [IO.Directory]::CreateDirectory($destinationPath) | Out-Null
        $created = $true
        $renderedDeployPath = Join-Path $destinationPath 'deploy'
        [IO.Directory]::CreateDirectory($renderedDeployPath) | Out-Null

        foreach ($sourceItem in Get-ChildItem -LiteralPath $sourcePath -Force) {
            if ($sourceItem.Name -ceq 'secrets') {
                continue
            }
            if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Deployment assets cannot contain a reparse point.'
            }
            if ($sourceItem.PSIsContainer) {
                $reparsePoint = Get-ChildItem -LiteralPath $sourceItem.FullName -Recurse -Force `
                    | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } `
                    | Select-Object -First 1
                if ($null -ne $reparsePoint) {
                    throw 'Deployment assets cannot contain a reparse point.'
                }
            }
            Copy-Item -LiteralPath $sourceItem.FullName -Destination $renderedDeployPath -Recurse -Force
        }

        $composePath = Join-Path $repositoryPath 'compose.yaml'
        if (-not (Test-Path -LiteralPath $composePath -PathType Leaf) `
            -or ((Get-Item -LiteralPath $composePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The compose template must be an ordinary file.'
        }
        Copy-Item -LiteralPath $composePath -Destination (Join-Path $destinationPath 'compose.yaml')

        if (Test-Path -LiteralPath (Join-Path $renderedDeployPath 'secrets')) {
            throw 'The rendered assets contain the private profile directory.'
        }

        $tokenCounts = @{}
        foreach ($token in $script:Mk8TokenByProperty.Keys) {
            $tokenCounts[$token] = 0
        }
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        foreach ($file in Get-ChildItem -LiteralPath $destinationPath -File -Recurse -Force) {
            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            if ([Array]::IndexOf($bytes, [byte]0) -ge 0) {
                continue
            }
            try {
                $content = $utf8.GetString($bytes)
            }
            catch {
                throw "A deployment asset is not valid UTF-8: $($file.Name)."
            }

            $rendered = $content
            foreach ($token in $script:Mk8TokenByProperty.Keys) {
                $count = [regex]::Matches($rendered, [regex]::Escape($token)).Count
                if ($count -gt 0) {
                    $tokenCounts[$token] += $count
                    $propertyName = $script:Mk8TokenByProperty[$token]
                    $rendered = $rendered.Replace($token, $profile.$propertyName, [StringComparison]::Ordinal)
                }
            }
            if ([regex]::IsMatch($rendered, '@@MK8_[A-Z0-9_]+@@')) {
                throw "A deployment asset contains an unknown token: $($file.Name)."
            }
            if ($rendered -cne $content) {
                [IO.File]::WriteAllText($file.FullName, $rendered, [Text.UTF8Encoding]::new($false))
            }
        }

        foreach ($token in $script:Mk8TokenByProperty.Keys) {
            if ($tokenCounts[$token] -eq 0) {
                throw "A required deployment token is missing: $token."
            }
        }

        return $destinationPath
    }
    catch {
        if ($created -and (Test-Path -LiteralPath $destinationPath)) {
            Remove-Item -LiteralPath $destinationPath -Recurse -Force
        }
        throw
    }
}

Export-ModuleMember -Function Import-Mk8DeploymentProfile, New-Mk8RenderedDeployAssets
