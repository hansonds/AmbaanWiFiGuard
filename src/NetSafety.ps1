# NetSafety.ps1 - shared checks for Wi-Fi Network Analyzer Pro V5
# Dot-source this file. Read-only: nothing here changes any setting.
#   Get-DnsSafety   -> is DNS working, is there a dead/typo server, is scam-link filtering on?
#   Invoke-LanScan  -> best-effort list of devices on the local /24 network

$script:DnsFilterResolvers = @{
    '1.1.1.2'='Cloudflare scam & malware filter'; '1.0.0.2'='Cloudflare scam & malware filter'
    '2606:4700:4700::1112'='Cloudflare scam & malware filter'; '2606:4700:4700::1002'='Cloudflare scam & malware filter'
    '1.1.1.3'='Cloudflare family filter'; '1.0.0.3'='Cloudflare family filter'
    '2606:4700:4700::1113'='Cloudflare family filter'; '2606:4700:4700::1003'='Cloudflare family filter'
    '9.9.9.9'='Quad9 malware filter'; '149.112.112.112'='Quad9 malware filter'
    '2620:fe::fe'='Quad9 malware filter'; '2620:fe::9'='Quad9 malware filter'
    '94.140.14.14'='AdGuard ad & malware filter'; '94.140.15.15'='AdGuard ad & malware filter'
    '2a10:50c0::ad1:ff'='AdGuard ad & malware filter'; '2a10:50c0::ad2:ff'='AdGuard ad & malware filter'
    '94.140.14.15'='AdGuard family filter'; '94.140.15.16'='AdGuard family filter'
    '185.228.168.9'='CleanBrowsing security filter'; '185.228.169.9'='CleanBrowsing security filter'
    '208.67.222.123'='OpenDNS FamilyShield'; '208.67.220.123'='OpenDNS FamilyShield'
}
$script:DnsPlainResolvers = @{
    '8.8.8.8'='Google DNS (no scam filter)'; '8.8.4.4'='Google DNS (no scam filter)'
    '2001:4860:4860::8888'='Google DNS (no scam filter)'; '2001:4860:4860::8844'='Google DNS (no scam filter)'
    '1.1.1.1'='Cloudflare DNS (no scam filter)'; '1.0.0.1'='Cloudflare DNS (no scam filter)'
    '2606:4700:4700::1111'='Cloudflare DNS (no scam filter)'; '2606:4700:4700::1001'='Cloudflare DNS (no scam filter)'
    '208.67.222.222'='OpenDNS'; '208.67.220.220'='OpenDNS'
}
# Common one-digit typos of well-known resolvers.
$script:DnsTypos = @{ '8.8.8.4'='8.8.4.4'; '8.8.4.8'='8.8.8.8'; '8.8.8.0'='8.8.8.8'; '1.1.1.0'='1.0.0.1'; '1.0.0.0'='1.0.0.1'; '1.1.1.12'='1.1.1.2' }

function ConvertTo-NormalIp([string]$ip) {
    try { return ([System.Net.IPAddress]::Parse($ip.Split('%')[0])).ToString().ToLower() } catch { return $ip.ToLower() }
}
function Test-PrivateIp([string]$ip) {
    if ($ip -match '^(10\.|192\.168\.|127\.|169\.254\.)') { return $true }
    if ($ip -match '^172\.(1[6-9]|2\d|3[01])\.') { return $true }
    if ($ip -match '^(fe80|fc|fd|::1)') { return $true }
    return $false
}
function Test-GlobalIpv6 {
    $a = Get-NetIPAddress -AddressFamily IPv6 -ErrorAction SilentlyContinue |
        Where-Object { $_.AddressState -eq 'Preferred' -and $_.IPAddress -notmatch '^(fe80|fd|fc|::1)' }
    return [bool]$a
}

function Get-V6Prefix([string]$ip) {
    # First 64 bits of an IPv6 address, e.g. 2409:40f3:1105:a284
    try {
        $b = ([System.Net.IPAddress]::Parse($ip.Split('%')[0])).GetAddressBytes()
        return (0, 2, 4, 6 | ForEach-Object { '{0:x}' -f (($b[$_] * 256) + $b[$_ + 1]) }) -join ':'
    } catch { return '' }
}

function Get-DnsSafety {
    $hasV6 = Test-GlobalIpv6
    # The router hands out its own global IPv6 address as DNS; it shares our /64 prefix.
    $localV6Prefixes = @(Get-NetIPAddress -AddressFamily IPv6 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^(fe80|fd|fc|::1)' } | ForEach-Object { Get-V6Prefix $_.IPAddress } | Select-Object -Unique)
    $adapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' })
    $adapterRows = @()
    $serverOrder = New-Object System.Collections.Generic.List[string]
    foreach ($a in $adapters) {
        $guid = $a.InterfaceGuid
        $v4 = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid" -Name NameServer -ErrorAction SilentlyContinue).NameServer
        $v6 = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\$guid" -Name NameServer -ErrorAction SilentlyContinue).NameServer
        $addrs = @(Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ })
        # fec0:0:0:ffff::1/2/3 are Windows' built-in placeholder IPv6 DNS (used when nobody hands out
        # an IPv6 DNS). They never answer and are not a real problem, so leave them out.
        $norm = @($addrs | ForEach-Object { ConvertTo-NormalIp $_ } | Where-Object { $_ -notmatch '^fec0:(0:0:)?:?ffff::[123]$' })
        $adapterRows += [pscustomobject]@{ Adapter = $a.Name; Servers = $norm; Static = [bool]($v4 -or $v6) }
        foreach ($s in $norm) { if (-not $serverOrder.Contains($s)) { $serverOrder.Add($s) } }
    }

    $servers = @()
    foreach ($s in $serverOrder) {
        $isV6 = $s.Contains(':')
        if ($script:DnsFilterResolvers.ContainsKey($s)) { $kind = 'Filter'; $label = $script:DnsFilterResolvers[$s] }
        elseif ($script:DnsPlainResolvers.ContainsKey($s)) { $kind = 'Plain'; $label = $script:DnsPlainResolvers[$s] }
        elseif ((Test-PrivateIp $s) -or ($isV6 -and $localV6Prefixes -contains (Get-V6Prefix $s))) { $kind = 'Router'; $label = 'Router DNS (filtering unknown)' }
        else { $kind = 'Other'; $label = 'Unknown DNS provider' }
        $unused = ($isV6 -and -not $hasV6)
        $ok = $false; $ms = $null
        if (-not $unused) {
            try {
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $null = Resolve-DnsName 'example.com' -Server $s -Type A -DnsOnly -QuickTimeout -NoHostsFile -ErrorAction Stop
                $sw.Stop(); $ok = $true; $ms = [int]$sw.ElapsedMilliseconds
            } catch {}
        }
        $servers += [pscustomobject]@{
            Address = $s; Kind = $kind; Label = $label; Responds = $ok; Ms = $ms; Unused = $unused
            Typo = $(if ($script:DnsTypos.ContainsKey($s)) { $script:DnsTypos[$s] } else { $null })
        }
    }

    $live = @($servers | Where-Object { $_.Responds })
    $dead = @($servers | Where-Object { -not $_.Responds -and -not $_.Unused })
    $unfiltered = @($live | Where-Object { $_.Kind -ne 'Filter' })
    $issues = @()
    foreach ($d in $dead) {
        if ($d.Typo) { $issues += "$($d.Address) doesn't answer. Typo? Did you mean $($d.Typo)?" }
        else { $issues += "$($d.Address) doesn't answer." }
    }

    if ($servers.Count -eq 0) {
        $status = 'Bad'; $shield = 'Unknown'; $headline = 'No DNS servers found'
        $detail = 'No connected adapter reported a DNS server.'
    } elseif ($live.Count -eq 0) {
        $status = 'Bad'; $shield = 'Off'; $headline = 'DNS is not answering'
        $detail = 'None of the DNS servers replied. Websites will not open by name.'
    } else {
        if ($unfiltered.Count -eq 0) {
            $shield = 'On'
            $names = ($live | Select-Object -ExpandProperty Label -Unique) -join ', '
            $headline = "Scam filter ON - $names"
            $detail = 'Known phishing and malware sites are blocked on this PC.'
            $status = 'Good'
        } elseif ($unfiltered.Count -lt $live.Count) {
            $shield = 'Partial'
            $headline = 'Scam filter only partly on'
            $detail = 'Some DNS servers filter and some do not. Windows may use either, so scam links can still open.'
            $status = 'Warn'
        } else {
            $shield = 'Off'
            $kinds = $unfiltered | Select-Object -ExpandProperty Label -Unique
            $headline = 'Scam filter OFF'
            $detail = "Using $($kinds -join ' + '). Scam and phishing links are not blocked."
            $status = 'Warn'
        }
        if ($dead.Count) {
            if ($status -eq 'Good') { $status = 'Warn' }
            $headline = "$headline  |  $($dead.Count) dead server"
        }
    }

    [pscustomobject]@{
        Status = $status; Shield = $shield; Headline = $headline; Detail = $detail
        Issues = $issues; Servers = $servers; Adapters = $adapterRows
        StaticDns = [bool](@($adapterRows | Where-Object { $_.Static }).Count)
        CheckedAt = Get-Date
    }
}

function Get-PrimaryGateway {
    try {
        $r = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
            Sort-Object { $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric } |
            Select-Object -First 1
        if ($r) { return $r.NextHop }
    } catch {}
    return $null
}

function Invoke-LanScan {
    # Best-effort discovery: ping-sweep the /24, then read the Windows neighbour (ARP) table.
    # This is NOT the router's own client list: sleeping phones or devices that ignore ping can be missed.
    $gw = Get-PrimaryGateway
    $own = @{}
    Get-NetAdapter -Physical -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.MacAddress) { $own[$_.MacAddress.ToUpper()] = $_.Name }
    }
    if (-not $gw) { return [pscustomobject]@{ Gateway = $null; GatewayMac = $null; Own = $own; Rows = @() } }
    $prefix = (($gw.Split('.'))[0..2] -join '.') + '.'
    $pings = New-Object System.Collections.Generic.List[object]
    $tasks = New-Object System.Collections.Generic.List[object]
    foreach ($i in 1..254) {
        try { $p = New-Object System.Net.NetworkInformation.Ping; $pings.Add($p); $tasks.Add($p.SendPingAsync("$prefix$i", 400)) } catch {}
    }
    try { [void][System.Threading.Tasks.Task]::WaitAll($tasks.ToArray(), 3000) } catch {}
    $replied = @{}
    foreach ($t in $tasks) {
        try { if ($t.IsCompleted -and -not $t.IsFaulted -and $t.Result.Status -eq 'Success') { $replied[$t.Result.Address.ToString()] = $true } } catch {}
    }
    foreach ($p in $pings) { try { $p.Dispose() } catch {} }

    $rows = @{}
    $gwMac = $null
    Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like "$prefix*" -and $_.State -notin @('Unreachable','Incomplete') -and
                       $_.LinkLayerAddress -match '^([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}$' -and $_.LinkLayerAddress -notin @('FF-FF-FF-FF-FF-FF','00-00-00-00-00-00') -and
                       $_.IPAddress -notmatch '\.255$' } |
        ForEach-Object {
            $mac = $_.LinkLayerAddress.ToUpper()
            if ($_.IPAddress -eq $gw) { $gwMac = $mac }
            # Alive = answered this sweep's ping, or Windows confirmed it just now. "Stale" ARP entries linger for minutes after a device leaves.
            $alive = $replied.ContainsKey($_.IPAddress) -or $_.State -eq 'Reachable'
            if (-not $rows.ContainsKey($mac)) { $rows[$mac] = [pscustomobject]@{ IP = $_.IPAddress; MAC = $mac; Alive = $alive } }
            elseif ($alive) { $rows[$mac].Alive = $true; $rows[$mac].IP = $_.IPAddress }
        }
    # This PC's own adapters on the same network (Windows does not list itself in ARP).
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like "$prefix*" } | ForEach-Object {
        $ad = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
        if ($ad -and $ad.MacAddress) {
            $mac = $ad.MacAddress.ToUpper()
            if (-not $rows.ContainsKey($mac)) { $rows[$mac] = [pscustomobject]@{ IP = $_.IPAddress; MAC = $mac; Alive = $true } }
        }
    }
    $sorted = @($rows.Values | Sort-Object { [int](($_.IP.Split('.'))[3]) })
    [pscustomobject]@{ Gateway = $gw; GatewayMac = $gwMac; Own = $own; Rows = $sorted }
}

function Test-RandomMac([string]$mac) {
    # Locally administered bit set = private/randomised address (phones, tablets, laptops in privacy mode).
    if ($mac -match '^[0-9A-F]([26AE])') { return $true }
    return $false
}
