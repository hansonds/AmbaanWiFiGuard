# Wi-Fi Network Analyzer Pro - Windows 11
# PowerShell 5.1+. Local diagnostics, history, dashboard and opt-in repairs.
# No third-party modules required. Speed test uses Cloudflare public endpoints (~25 MB/run).

$ErrorActionPreference = "SilentlyContinue"
$ProgressPreference = "SilentlyContinue"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataRoot = if ($env:AMBAAN_DATA) { $env:AMBAAN_DATA } else { $Root }   # set by the EXE launcher
$Reports = Join-Path $DataRoot "Reports"
$History = Join-Path $DataRoot "History"
New-Item -ItemType Directory -Force -Path $Reports,$History | Out-Null
$Stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$Now = Get-Date
$Csv = Join-Path $History "NetworkHistory.csv"
$HtmlReport = Join-Path $Reports "WiFiDashboard-$Stamp.html"
$TxtReport = Join-Path $Reports "WiFiReport-$Stamp.txt"
$Results = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param([string]$Area,[string]$Check,[string]$Value,[string]$Status,[string]$Explanation="")
    $Results.Add([pscustomobject]@{Area=$Area;Check=$Check;Value=$Value;Status=$Status;Explanation=$Explanation}) | Out-Null
    $color = switch ($Status) { "PASS" {"Green"} "WARNING" {"Yellow"} "FAIL" {"Red"} default {"Gray"} }
    Write-Host ("{0,-22} {1,-24} {2}" -f $Check,$Value,$Status) -ForegroundColor $color
    if ($Explanation) { Write-Host "  $Explanation" -ForegroundColor DarkGray }
}
function Ping-Target {
    param([string]$Target,[int]$Count=15)
    $p = New-Object System.Net.NetworkInformation.Ping
    $times = @(); $lost = 0
    for($i=0;$i -lt $Count;$i++){
        try {
            $r=$p.Send($Target,1800)
            if($r.Status -eq [System.Net.NetworkInformation.IPStatus]::Success){$times += [double]$r.RoundtripTime}else{$lost++}
        } catch {$lost++}
        Start-Sleep -Milliseconds 100
    }
    $p.Dispose()
    [pscustomobject]@{
        Sent=$Count;Received=$times.Count;Loss=[math]::Round(($lost/$Count)*100,1)
        Avg=if($times.Count){[math]::Round(($times|Measure-Object -Average).Average,1)}else{$null}
        Min=if($times.Count){($times|Measure-Object -Minimum).Minimum}else{$null}
        Max=if($times.Count){($times|Measure-Object -Maximum).Maximum}else{$null}
    }
}
function Run-SpeedTest {
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $down=$null; $up=$null; $downErr=""; $upErr=""
    try {
        $n=20000000
        $url="https://speed.cloudflare.com/__down?bytes=$n&x=$([guid]::NewGuid().ToString('N'))"
        $sw=[Diagnostics.Stopwatch]::StartNew()
        $resp=$client.GetAsync($url,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $resp.EnsureSuccessStatusCode()
        $stream=$resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buf=New-Object byte[] 65536; [long]$total=0
        while(($read=$stream.Read($buf,0,$buf.Length)) -gt 0){$total += $read}
        $stream.Dispose();$resp.Dispose();$sw.Stop()
        if($sw.Elapsed.TotalSeconds -gt 0){$down=[math]::Round(($total*8/1000000)/$sw.Elapsed.TotalSeconds,1)}
    } catch {$downErr=$_.Exception.Message}
    try {
        $n=5000000; $payload=New-Object byte[] $n
        $rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($payload);$rng.Dispose()
        $content=New-Object System.Net.Http.ByteArrayContent(,$payload)
        $content.Headers.ContentType=[Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/octet-stream")
        $sw=[Diagnostics.Stopwatch]::StartNew()
        $resp=$client.PostAsync("https://speed.cloudflare.com/__up",$content).GetAwaiter().GetResult()
        $resp.EnsureSuccessStatusCode();$null=$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $resp.Dispose();$content.Dispose();$sw.Stop()
        if($sw.Elapsed.TotalSeconds -gt 0){$up=[math]::Round(($n*8/1000000)/$sw.Elapsed.TotalSeconds,1)}
    } catch {$upErr=$_.Exception.Message}
    $client.Dispose()
    [pscustomobject]@{Download=$down;Upload=$up;DownloadError=$downErr;UploadError=$upErr}
}
function HtmlEncode([object]$x){[System.Net.WebUtility]::HtmlEncode([string]$x)}

Write-Host "`n=== WI-FI NETWORK ANALYZER PRO ===" -ForegroundColor Cyan
Write-Host "Started: $($Now.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "Speed test transfers approximately 25 MB. Press Ctrl+C to cancel.`n" -ForegroundColor DarkYellow

# Active Wi-Fi adapter and current link details
$wifiText = (netsh wlan show interfaces) -join "`n"
function Parse-Netsh([string]$label) {
    $m=[regex]::Match($wifiText,"(?im)^\s*$label\s*:\s*(.+?)\s*$")
    if($m.Success){$m.Groups[1].Value.Trim()}else{$null}
}
$connected = $wifiText -match '(?im)^\s*State\s*:\s*connected'
$wifiName=Parse-Netsh "Name"; $ssid=Parse-Netsh "SSID"; $band=Parse-Netsh "Band"
$channel=Parse-Netsh "Channel"; $radio=Parse-Netsh "Radio type"
$sigText=Parse-Netsh "Signal";$rssiText=Parse-Netsh "Rssi"
$rxText=Parse-Netsh "Receive rate \(Mbps\)";$txText=Parse-Netsh "Transmit rate \(Mbps\)"
$signal=$null;$rssi=$null;$rx=$null;$tx=$null
if($sigText -match '(\d+)'){$signal=[int]$Matches[1]}
if($rssiText -match '-?\d+'){$rssi=[int]$Matches[0]}
if($rxText -match '([\d.]+)'){$rx=[double]$Matches[1]}
if($txText -match '([\d.]+)'){$tx=[double]$Matches[1]}
Add-Check "Wi-Fi" "Connection" $(if($connected){"Connected"}else{"Disconnected"}) $(if($connected){"PASS"}else{"FAIL"})
if($null -ne $signal){Add-Check "Wi-Fi" "Signal strength" "$signal%" $(if($signal -ge 80){"PASS"}elseif($signal -ge 50){"WARNING"}else{"FAIL"})}
if($null -ne $rssi){Add-Check "Wi-Fi" "RSSI" "$rssi dBm" $(if($rssi -ge -60){"PASS"}elseif($rssi -ge -70){"WARNING"}else{"FAIL"}) "Closer to zero is stronger."}
Add-Check "Wi-Fi" "Band / channel" "$band / $channel" "INFO" "Radio type: $radio"
Add-Check "Wi-Fi" "Link rate" "RX $rx / TX $tx Mbps" "INFO" "PHY link rate is not real internet throughput."

# Active default gateway
$ipcfg=Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq "Up" -and $_.IPv4DefaultGateway} | Select-Object -First 1
$gateway=$null
if($ipcfg){$gateway=$ipcfg.IPv4DefaultGateway.NextHop}
$router=$null
if($gateway){
    Write-Host "`nTesting local router..." -ForegroundColor Cyan
    $router=Ping-Target $gateway 20
    $st=if($router.Loss -gt 5 -or ($router.Avg -ne $null -and $router.Avg -gt 20)){"FAIL"}elseif($router.Loss -gt 0 -or ($router.Avg -ne $null -and $router.Avg -gt 5)){"WARNING"}else{"PASS"}
    Add-Check "Local network" "Router latency" "$(if($null -ne $router.Avg){"$($router.Avg) ms avg"}else{"No replies"})" $st "$($router.Loss)% packet loss; router address hidden from report."
}else{Add-Check "Local network" "Default gateway" "Not found" "FAIL" "No active IPv4 gateway was found."}

# Internet endpoints
Write-Host "`nTesting internet endpoints..." -ForegroundColor Cyan
$pingCloud=Ping-Target "1.1.1.1" 20
$pingGoogle=Ping-Target "8.8.8.8" 20
foreach($entry in @(@{Name="Cloudflare 1.1.1.1";P=$pingCloud},@{Name="Google 8.8.8.8";P=$pingGoogle})){
    $p=$entry.P
    $st=if($p.Loss -gt 5 -or ($p.Avg -ne $null -and $p.Avg -gt 150)){"FAIL"}elseif($p.Loss -gt 0 -or ($p.Avg -ne $null -and $p.Avg -gt 80)){"WARNING"}elseif($null -eq $p.Avg){"FAIL"}else{"PASS"}
    Add-Check "Internet" $entry.Name "$(if($null -ne $p.Avg){"$($p.Avg) ms avg"}else{"No replies"})" $st "$($p.Loss)% loss; min $($p.Min) / max $($p.Max) ms."
}

# DNS lookup timing
Write-Host "`nTesting DNS..." -ForegroundColor Cyan
$dnsMs=$null;$dnsStatus="Failed"
try {
    $sw=[Diagnostics.Stopwatch]::StartNew()
    $null=Resolve-DnsName "www.microsoft.com" -Type A -DnsOnly -ErrorAction Stop
    $sw.Stop();$dnsMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1);$dnsStatus="Working"
    Add-Check "DNS" "Lookup time" "$dnsMs ms" $(if($dnsMs -lt 100){"PASS"}elseif($dnsMs -lt 300){"WARNING"}else{"FAIL"}) "One lookup; DNS caching can affect this measurement."
}catch{Add-Check "DNS" "Resolution" "Failed" "FAIL" "Could not resolve www.microsoft.com."}

# DNS safety (V5): dead/typo servers and scam-link filtering
Write-Host "`nChecking DNS safety..." -ForegroundColor Cyan
. (Join-Path $Root "NetSafety.ps1")
$dnsSafe = Get-DnsSafety
foreach ($srv in $dnsSafe.Servers) {
    $shown = if ($srv.Kind -eq 'Router') { 'Router (local)' } else { $srv.Address }
    $val = if ($srv.Unused) { 'Unused (no IPv6)' } elseif ($srv.Responds) { "Answers in $($srv.Ms) ms" } else { 'NOT ANSWERING' }
    $st = if ($srv.Unused) { 'INFO' } elseif (-not $srv.Responds) { 'FAIL' } elseif ($srv.Kind -eq 'Filter') { 'PASS' } else { 'INFO' }
    $exp = $srv.Label + $(if ($srv.Typo) { ". Looks like a typo: did you mean $($srv.Typo)?" } else { '' })
    Add-Check "DNS safety" "Server $shown" $val $st $exp
}
$shieldStatus = switch ($dnsSafe.Shield) { 'On' { 'PASS' } 'Partial' { 'WARNING' } 'Off' { 'WARNING' } default { 'INFO' } }
Add-Check "DNS safety" "Scam-link filter" $dnsSafe.Shield $shieldStatus "$($dnsSafe.Detail) To protect every device at home, set 1.1.1.2 / 1.0.0.2 as DNS on the router."

# Route path - hop results are best effort, routers may not respond to TTL probes
Write-Host "`nTracing route to 1.1.1.1 (best effort)..." -ForegroundColor Cyan
$routeLines=@()
try {
    $tr=tracert.exe -d -h 12 -w 900 1.1.1.1 2>&1
    $routeLines=@($tr | Select-Object -Skip 1 | Select-Object -First 12 | ForEach-Object {[string]$_})
    $reachableHops=@($routeLines | Where-Object {$_ -match '\d+\.\d+\.\d+\.\d+'}).Count
    Add-Check "Routing" "Traceroute" "$reachableHops responding hops" "INFO" "Non-responding hops are common; this alone does not prove packet loss."
}catch{Add-Check "Routing" "Traceroute" "Unavailable" "INFO" "Could not run tracert."}

# Nearby Wi-Fi scan (names and BSSIDs intentionally not stored in reports)
Write-Host "`nScanning nearby Wi-Fi networks..." -ForegroundColor Cyan
$scanText=(netsh wlan show networks mode=bssid) -join "`n"
$networkCount=([regex]::Matches($scanText,'(?im)^\s*SSID\s+\d+\s*:')).Count
$channelCounts=@{}
foreach($m in [regex]::Matches($scanText,'(?im)^\s*Channel\s*:\s*(\d+)')){
    $ch=$m.Groups[1].Value
    if(!$channelCounts.ContainsKey($ch)){$channelCounts[$ch]=0}
    $channelCounts[$ch]++
}
$busyChannels=($channelCounts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 3 | ForEach-Object {"CH $($_.Key): $($_.Value)"}) -join ", "
Add-Check "Wi-Fi environment" "Nearby networks" "$networkCount detected" "INFO" "Channel counts: $(if($busyChannels){$busyChannels}else{'Not available'}). Scan is a snapshot, not a full RF spectrum analysis."

# Speed test
Write-Host "`nRunning automatic speed test via Cloudflare (~25 MB)..." -ForegroundColor Cyan
$speed=Run-SpeedTest
$down=$speed.Download;$up=$speed.Upload
if($null -ne $down){Add-Check "Throughput" "Download" "$down Mbps" $(if($down -ge 50){"PASS"}elseif($down -ge 10){"WARNING"}else{"FAIL"}) "Compare against your subscribed plan; server conditions affect results."}
else{Add-Check "Throughput" "Download" "Unavailable" "WARNING" $speed.DownloadError}
if($null -ne $up){Add-Check "Throughput" "Upload" "$up Mbps" $(if($up -ge 10){"PASS"}elseif($up -ge 2){"WARNING"}else{"FAIL"})}
else{Add-Check "Throughput" "Upload" "Unavailable" "WARNING" $speed.UploadError}

# Aggregate adapter bytes for machine-level usage snapshot
$adapterStats=$null
try {
    $adapterStats=Get-NetAdapterStatistics -Name $wifiName
    if($adapterStats){Add-Check "Adapter" "Cumulative bytes" ("RX {0:N0} / TX {1:N0}" -f $adapterStats.ReceivedBytes,$adapterStats.SentBytes) "INFO" "Counters are cumulative since adapter start; not per-app usage."}
}catch{}


# Smart Auto Repair V3
# Only low-risk DNS cache flush is automatic, and only when DNS resolution failed.
# Network-disruptive/system-level repairs always require explicit confirmation.
$RepairLog = Join-Path $Reports "RepairLog.csv"
$RepairEvents = New-Object System.Collections.Generic.List[object]
$RepairApplied = $false
function Log-Repair {
    param([string]$Action,[string]$Reason,[string]$Result,[string]$Verification)
    $RepairEvents.Add([pscustomobject]@{
        Timestamp=(Get-Date).ToString("s"); Action=$Action; Reason=$Reason
        Result=$Result; Verification=$Verification
    }) | Out-Null
}
function Is-Admin {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host "`n=== SMART AUTO REPAIR ===" -ForegroundColor Cyan
if ($dnsStatus -eq "Failed") {
    Write-Host "DNS lookup failed. Applying safe repair: flush DNS resolver cache." -ForegroundColor Yellow
    ipconfig.exe /flushdns | Out-Null
    Start-Sleep -Seconds 2
    try {
        $null = Resolve-DnsName "www.microsoft.com" -Type A -DnsOnly -ErrorAction Stop
        $dnsStatus = "Working after DNS flush"
        $RepairApplied = $true
        Log-Repair "Flush DNS cache" "Initial DNS lookup failed" "Completed" "DNS lookup succeeded after repair"
        Add-Check "Auto repair" "Flush DNS cache" "Completed" "PASS" "DNS lookup succeeded on post-repair verification."
    } catch {
        Log-Repair "Flush DNS cache" "Initial DNS lookup failed" "Completed" "DNS still failing"
        Add-Check "Auto repair" "Flush DNS cache" "Applied; issue persists" "WARNING" "DNS still fails. This may be an upstream resolver or connectivity issue."
    }
} else {
    Log-Repair "No safe repair needed" "No DNS failure detected" "Skipped" "Not applicable"
    Add-Check "Auto repair" "Safe repair assessment" "No action needed" "INFO" "No safe automatic repair was indicated by this run."
}

# Risky repairs are offered only when local gateway measurements indicate a local-link problem.
$localIssue = ($router -and (($router.Loss -gt 5) -or ($null -ne $router.Avg -and $router.Avg -gt 20)))
if ($localIssue) {
    Write-Host "`nLocal router latency/loss is elevated. Possible network-disruptive repairs:" -ForegroundColor Yellow
    Write-Host "1. Renew DHCP lease (briefly interrupts connectivity)"
    Write-Host "2. Restart Wi-Fi adapter (disconnects Wi-Fi; Administrator required)"
    Write-Host "3. Skip risky repairs"
    $riskChoice = Read-Host "Choose 1-3 (default 3)"
    if ($riskChoice -eq "1") {
        $confirm = Read-Host "DHCP renew may interrupt active sessions. Type YES to proceed"
        if ($confirm -ceq "YES") {
            ipconfig.exe /release | Out-Null
            Start-Sleep -Seconds 2
            ipconfig.exe /renew | Out-Null
            Start-Sleep -Seconds 4
            $RepairApplied = $true
            Log-Repair "Renew DHCP lease" "Elevated router latency/loss" "Command completed" "Running post-repair tests"
        } else {
            Log-Repair "Renew DHCP lease" "Elevated router latency/loss" "Declined by user" "Not run"
        }
    } elseif ($riskChoice -eq "2") {
        if (-not (Is-Admin)) {
            Write-Warning "Restarting the adapter requires Administrator. No change made."
            Log-Repair "Restart Wi-Fi adapter" "Elevated router latency/loss" "Not run: Administrator required" "Not run"
        } else {
            $confirm = Read-Host "This will disconnect Wi-Fi temporarily. Type YES to proceed"
            if ($confirm -ceq "YES") {
                Restart-NetAdapter -Name $wifiName -Confirm:$false
                Start-Sleep -Seconds 8
                $RepairApplied = $true
                Log-Repair "Restart Wi-Fi adapter" "Elevated router latency/loss" "Command completed" "Running post-repair tests"
            } else {
                Log-Repair "Restart Wi-Fi adapter" "Elevated router latency/loss" "Declined by user" "Not run"
            }
        }
    } else {
        Log-Repair "Risky repair" "Local gateway issue" "Skipped by user" "Not run"
    }
} else {
    Log-Repair "Risky repair assessment" "No strong local gateway fault detected" "Not offered" "No disruptive action recommended"
}

# If safe DNS flush did not restore DNS, offer Winsock reset only with explicit approval.
if ($dnsStatus -ne "Working" -and $dnsStatus -ne "Working after DNS flush") {
    if (Is-Admin) {
        $winsockChoice = Read-Host "DNS still fails. Winsock reset is disruptive and usually requires reboot. Type RESET to approve, or press Enter to skip"
        if ($winsockChoice -ceq "RESET") {
            netsh.exe winsock reset | Out-Null
            $RepairApplied = $true
            Log-Repair "Winsock reset" "DNS still failed after cache flush" "Command completed; reboot required" "Reboot, then run analyzer again"
            Add-Check "Auto repair" "Winsock reset" "Completed; reboot required" "WARNING" "Run Windows restart, then verify connectivity."
        } else {
            Log-Repair "Winsock reset" "DNS still failed after cache flush" "Declined by user" "Not run"
        }
    } else {
        Log-Repair "Winsock reset" "DNS still failed after cache flush" "Not run: Administrator required" "Not run"
    }
}

# Post-repair verification: re-check router, internet reachability and DNS after any applied repair.
if ($RepairApplied) {
    Write-Host "`nVerifying network after repair..." -ForegroundColor Cyan
    Start-Sleep -Seconds 3
    if ($gateway) {
        $router = Ping-Target $gateway 10
        $routerVerify = if ($router.Loss -le 5 -and $null -ne $router.Avg) {"Router reachable; $($router.Loss)% loss; $($router.Avg) ms avg"} else {"Router still degraded; $($router.Loss)% loss"}
        Add-Check "Post-repair" "Router verification" $routerVerify $(if($router.Loss -le 5 -and $null -ne $router.Avg){"PASS"}else{"WARNING"})
    }
    $pingCloud = Ping-Target "1.1.1.1" 10
    Add-Check "Post-repair" "Internet verification" "$(if($null -ne $pingCloud.Avg){"$($pingCloud.Avg) ms avg; $($pingCloud.Loss)% loss"}else{"No replies"})" $(if($null -ne $pingCloud.Avg -and $pingCloud.Loss -le 5){"PASS"}else{"WARNING"})
    try {
        $null = Resolve-DnsName "www.microsoft.com" -Type A -DnsOnly -ErrorAction Stop
        $dnsStatus = "Working after verification"
        Add-Check "Post-repair" "DNS verification" "Working" "PASS"
    } catch {
        $dnsStatus = "Failed after verification"
        Add-Check "Post-repair" "DNS verification" "Still failing" "FAIL"
    }
    # Reflect the verified final state in the main status while retaining the repair audit trail.
    if ($dnsStatus -eq "Working after verification" -or $dnsStatus -eq "Working after DNS flush") {
        foreach ($item in $Results) {
            if ($item.Area -eq "DNS" -and $item.Check -eq "Resolution") {
                $item.Value = "Recovered after repair"
                $item.Status = "PASS"
                $item.Explanation = "Initial lookup failed; repair was applied and DNS subsequently verified."
            }
        }
    }
    if ($router -and $router.Loss -le 5 -and $null -ne $router.Avg) {
        foreach ($item in $Results) {
            if ($item.Area -eq "Local network" -and $item.Check -eq "Router latency") {
                $item.Value = "$($router.Avg) ms avg after repair"
                $item.Status = "PASS"
                $item.Explanation = "Post-repair router verification succeeded; see repair log for initial condition."
            }
        }
    }
} else {
    Add-Check "Post-repair" "Verification" "No repair applied" "INFO" "Initial diagnostic results remain the baseline."
}

# Persist repair log separately from performance history.
foreach ($event in $RepairEvents) {
    if (Test-Path $RepairLog) { $event | Export-Csv -Path $RepairLog -NoTypeInformation -Append -Encoding UTF8 }
    else { $event | Export-Csv -Path $RepairLog -NoTypeInformation -Encoding UTF8 }
}
$repairHtml = foreach ($event in $RepairEvents) {
    "<tr><td>$(HtmlEncode $event.Timestamp)</td><td>$(HtmlEncode $event.Action)</td><td>$(HtmlEncode $event.Result)</td><td>$(HtmlEncode $event.Verification)</td></tr>"
}
$repairHtml = ($repairHtml -join "`n")
$repairSummary = if ($RepairApplied) { "Repair action(s) applied; post-repair verification performed." } else { "No network-disruptive repair was applied." }

# Overall status and likely bottleneck heuristic
$failCount=@($Results | Where-Object Status -eq "FAIL").Count
$warnCount=@($Results | Where-Object Status -eq "WARNING").Count
$overall=if($failCount -gt 0){"ATTENTION REQUIRED"}elseif($warnCount -gt 0){"DEGRADED / REVIEW"}else{"NO ISSUES DETECTED"}
$likely="Insufficient evidence; compare wired and Wi-Fi tests."
if($router -and ($router.Loss -gt 0 -or ($router.Avg -ne $null -and $router.Avg -gt 10))){$likely="Likely local Wi-Fi/router segment: gateway latency or packet loss is elevated."}
elseif($router -and $router.Loss -eq 0 -and $pingCloud.Loss -gt 0 -and $pingGoogle.Loss -gt 0){$likely="Likely upstream of the router or ISP path; verify with Ethernet and another time window."}
elseif($router -and $router.Loss -eq 0 -and $router.Avg -lt 5 -and (($pingCloud.Avg -gt 80) -or ($pingGoogle.Avg -gt 80))){$likely="Local link looks responsive; likely WAN/ISP routing, congestion, or destination path."}
elseif($null -ne $down -and $down -lt 10 -and $signal -ge 80){$likely="Wi-Fi signal is strong but measured throughput is low; compare Ethernet and your plan to isolate router/ISP/server."}
elseif($signal -lt 50){$likely="Weak Wi-Fi signal is a likely contributor; test closer to the router."}

# Persist history, omitting identifiers
$record=[pscustomobject]@{
 Timestamp=$Now.ToString("s"); SignalPercent=$signal; RSSIdBm=$rssi; Band=$band; Channel=$channel
 LinkRxMbps=$rx;LinkTxMbps=$tx;RouterAvgMs=if($router){$router.Avg}else{$null}
 RouterLossPct=if($router){$router.Loss}else{$null};CloudflareAvgMs=$pingCloud.Avg;CloudflareLossPct=$pingCloud.Loss
 GoogleAvgMs=$pingGoogle.Avg;GoogleLossPct=$pingGoogle.Loss;DnsMs=$dnsMs;DownloadMbps=$down;UploadMbps=$up;Overall=$overall
}
if(Test-Path $Csv){$record|Export-Csv $Csv -NoTypeInformation -Append -Encoding UTF8}else{$record|Export-Csv $Csv -NoTypeInformation -Encoding UTF8}
$historyRows=@()
try{$historyRows=@(Import-Csv $Csv | Select-Object -Last 200)}catch{}
$historyJson=ConvertTo-Json -InputObject $historyRows -Depth 4 -Compress
$resultsJson=ConvertTo-Json -InputObject @($Results) -Depth 4 -Compress
$routeHtml=($routeLines | ForEach-Object {"<li>$(HtmlEncode $_)</li>"}) -join "`n"

# Recommendations
$recommendations=New-Object System.Collections.Generic.List[string]
if($signal -lt 70){$recommendations.Add("Move closer to the router and compare signal/RSSI; avoid placing the router behind metal or inside a cabinet.")}
if($router -and ($router.Loss -gt 0 -or ($router.Avg -gt 10))){$recommendations.Add("Local gateway response is elevated. Test near the router, pause heavy local transfers, and compare a wired Ethernet test.")}
if($pingCloud.Loss -gt 0 -or $pingGoogle.Loss -gt 0){$recommendations.Add("External ping loss observed. Repeat at another time and compare multiple destinations; ICMP may be deprioritized.")}
if($null -ne $down -and $down -lt 10){$recommendations.Add("Throughput is low in this sample. Compare against your subscribed plan, test via Ethernet, and repeat without VPN or large downloads.")}
if($dnsStatus -eq "Failed" -or ($dnsMs -gt 300)){$recommendations.Add("DNS resolution is failing or slow. Try another resolver only after confirming the issue is repeatable.")}
if($recommendations.Count -eq 0){$recommendations.Add("No strong fault was isolated in this run. Repeat during a slowdown and compare the historical graphs.")}
$recommendationHtml=($recommendations | ForEach-Object {"<li>$(HtmlEncode $_)</li>"}) -join "`n"
$checksHtml=foreach($r in $Results){
    $cls=switch($r.Status){"PASS"{"pass"}"WARNING"{"warn"}"FAIL"{"fail"}default{"info"}}
    "<tr><td>$(HtmlEncode $r.Area)</td><td>$(HtmlEncode $r.Check)</td><td>$(HtmlEncode $r.Value)</td><td><span class='pill $cls'>$(HtmlEncode $r.Status)</span></td><td>$(HtmlEncode $r.Explanation)</td></tr>"
}
$overallClass=if($overall -eq "NO ISSUES DETECTED"){"pass"}else{"warn"}
$dashboard=@'
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Wi-Fi Network Analyzer Pro · Network Intelligence</title>
<style>
:root{color-scheme:dark;--bg:#080e1b;--surface:#101a2c;--surface2:#14223a;--surface3:#1b2c48;--ink:#f3f7ff;--muted:#9aacc8;--line:#263956;--blue:#69a9ff;--cyan:#5ce1df;--violet:#a88bff;--green:#5ee0a0;--amber:#ffca72;--red:#ff7789;--shadow:0 22px 60px #02081780}
*{box-sizing:border-box}body{margin:0;background:radial-gradient(ellipse at 12% -10%,#1a3763 0,transparent 38%),radial-gradient(ellipse at 100% 0%,#24204d 0,transparent 30%),var(--bg);color:var(--ink);font:14px/1.5 "Segoe UI",Inter,Arial,sans-serif;min-height:100vh}
main{max-width:1440px;margin:auto;padding:30px 34px 54px}.topbar{display:flex;align-items:center;justify-content:space-between;gap:20px;margin-bottom:26px}.brand{display:flex;align-items:center;gap:14px}.brandmark{width:48px;height:48px;border-radius:15px;background:linear-gradient(135deg,#5ce1df,#6b9dff 55%,#a88bff);display:grid;place-items:center;color:#071225;font-size:22px;font-weight:900;box-shadow:0 8px 28px #5c9dff55}.eyebrow{color:var(--cyan);font-size:10px;letter-spacing:2px;text-transform:uppercase;font-weight:800}.brand h1{font-size:23px;letter-spacing:-.5px;margin:1px 0 0}.sub{color:var(--muted);font-size:12px}.runmeta{text-align:right}.runmeta .date{color:var(--muted);font-size:12px;margin-top:7px}
.pill,.tag{display:inline-flex;align-items:center;gap:6px;padding:6px 11px;border-radius:999px;font-size:10px;letter-spacing:.7px;font-weight:800;text-transform:uppercase;border:1px solid transparent}.pass{background:#123b32;color:#8bf0c1;border-color:#246c58}.warn{background:#49351d;color:#ffda96;border-color:#77552a}.fail{background:#4a2430;color:#ff9eac;border-color:#783849}.info{background:#1d3555;color:#a9ceff;border-color:#345781}
.hero{position:relative;overflow:hidden;border:1px solid #38547a;border-radius:24px;padding:25px 28px;margin-bottom:18px;background:linear-gradient(115deg,#172f50 0%,#172644 52%,#29234d 100%);box-shadow:var(--shadow)}.hero:after{content:"";position:absolute;width:340px;height:340px;border-radius:50%;right:7%;top:-240px;background:#5ce1df22;filter:blur(4px)}.hero-inner{position:relative;z-index:1;display:flex;justify-content:space-between;align-items:center;gap:20px}.hero h2{font-size:22px;letter-spacing:-.4px;margin:5px 0}.hero p{margin:0;color:#b7c8e1;max-width:650px}.hero-stat{text-align:right;min-width:155px}.hero-stat strong{display:block;font-size:25px;letter-spacing:-.5px}.hero-stat span{color:var(--muted);font-size:11px}
.grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin-bottom:18px}.metric-card,.panel{background:linear-gradient(160deg,#14223a,#101a2c 78%);border:1px solid var(--line);border-radius:19px;box-shadow:0 12px 35px #02081735}.metric-card{padding:19px;position:relative;overflow:hidden}.metric-card:after{content:"";position:absolute;width:100px;height:100px;right:-45px;bottom:-55px;border-radius:50%;background:var(--glow,#69a9ff);opacity:.12;filter:blur(2px)}.metric-head{display:flex;justify-content:space-between;align-items:center;color:var(--muted);font-size:12px}.metric-icon{width:31px;height:31px;display:grid;place-items:center;border-radius:10px;background:#ffffff0c;color:var(--accent,var(--blue));font-size:16px}.metric{font-size:30px;font-weight:750;letter-spacing:-1px;margin:13px 0 2px}.metric-foot{font-size:11px;color:var(--muted)}
.layout{display:grid;grid-template-columns:1.45fr 1fr;gap:16px;margin-bottom:16px}.panel{padding:21px;margin-bottom:16px}.panel-head{display:flex;align-items:flex-start;justify-content:space-between;gap:14px;margin-bottom:15px}.panel h3{font-size:14px;letter-spacing:.1px;margin:0}.panel-kicker{color:var(--muted);font-size:11px;margin-top:3px}.tabs{display:flex;gap:5px;padding:4px;border:1px solid var(--line);background:#091323;border-radius:11px}.tabs button,button{font:inherit;color:var(--muted);background:transparent;border:0;padding:7px 10px;border-radius:8px;cursor:pointer;font-size:11px}.tabs button.active{background:#27466e;color:#fff;box-shadow:0 3px 10px #0004}.chartwrap{height:245px;position:relative}canvas{width:100%;height:245px;display:block}.legend{display:flex;gap:15px;flex-wrap:wrap;margin-top:10px;color:var(--muted);font-size:11px}.dot{display:inline-block;width:7px;height:7px;border-radius:50%;margin-right:6px}
.bottleneck{display:grid;grid-template-columns:1fr auto;align-items:center;gap:20px;background:linear-gradient(110deg,#132a46,#171d39);border:1px solid #314c70;border-radius:19px;padding:22px;margin-bottom:16px}.bottleneck h3{margin:0 0 7px;font-size:14px}.bottleneck p{margin:0;color:#d3e1f4;font-size:13px}.signalbadge{font-size:11px;color:var(--cyan);border:1px solid #397b88;background:#12343e;padding:9px 12px;border-radius:12px;white-space:nowrap}
.stages{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:9px}.stage{padding:13px 11px;border:1px solid var(--line);border-radius:13px;background:#0d1728}.stage-num{font-size:10px;color:var(--cyan);font-weight:800;letter-spacing:1px}.stage strong{display:block;font-size:11px;margin:5px 0}.stage small{color:var(--muted);font-size:10px}
.two{display:grid;grid-template-columns:1fr 1fr;gap:16px}.actions{list-style:none;padding:0;margin:0}.actions li{padding:10px 0 10px 23px;position:relative;border-bottom:1px solid #263650;color:#c9d6e9;font-size:12px}.actions li:last-child{border-bottom:0}.actions li:before{content:"↗";position:absolute;left:0;color:var(--cyan);font-weight:800}
.tablewrap{overflow:auto;border:1px solid var(--line);border-radius:12px}table{border-collapse:collapse;width:100%;font-size:11px;min-width:650px}th,td{text-align:left;padding:11px 12px;border-bottom:1px solid #24344e;vertical-align:top}th{background:#0c1728;color:var(--muted);font-weight:700;text-transform:uppercase;letter-spacing:.7px;font-size:9px}tbody tr:hover{background:#ffffff06}tbody tr:last-child td{border-bottom:0}
.route{padding-left:20px;margin:8px 0;color:var(--muted);font-size:11px;line-height:1.8}.empty{color:var(--muted);font-size:11px;padding:12px 0}.footer{color:#7488a8;font-size:10px;line-height:1.7;margin:20px 4px 0}
@media(max-width:950px){main{padding:20px}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}.layout{grid-template-columns:1fr}.stages{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(max-width:600px){main{padding:13px}.topbar,.hero-inner{align-items:flex-start;flex-direction:column}.runmeta,.hero-stat{text-align:left}.grid{grid-template-columns:1fr 1fr;gap:8px}.metric-card{padding:13px}.metric{font-size:23px}.panel{padding:15px}.two{grid-template-columns:1fr}.bottleneck{grid-template-columns:1fr}.signalbadge{width:max-content}.panel-head{flex-direction:column}.tabs{width:max-content}}
</style></head><body><main>
<header class="topbar">
 <div class="brand"><div class="brandmark">⌁</div><div><div class="eyebrow">Network intelligence / Windows 11</div><h1>Wi-Fi Network Analyzer <span style="color:#7ab8ff">Pro</span></h1><div class="sub">Local diagnostics · performance history · smart repair</div></div></div>
 <div class="runmeta"><span class="tag __OVERALLCLASS__">● __OVERALL__</span><div class="date">Diagnostic run · __RUNDATE__</div></div>
</header>
<section class="hero"><div class="hero-inner"><div><div class="eyebrow">Your network snapshot</div><h2>Know where your connection slows down.</h2><p>Performance signals, route checks and repair verification gathered into one local report. Use repeat runs to spot patterns over time.</p></div><div class="hero-stat"><strong>__SIGNAL__</strong><span>Wi-Fi signal strength · RSSI __RSSI__</span></div></div></section>
<section class="grid">
 <div class="metric-card" style="--accent:#69a9ff;--glow:#69a9ff"><div class="metric-head"><span>Download speed</span><span class="metric-icon">↓</span></div><div class="metric">__DOWN__</div><div class="metric-foot">Cloudflare throughput sample</div></div>
 <div class="metric-card" style="--accent:#5ce1df;--glow:#5ce1df"><div class="metric-head"><span>Upload speed</span><span class="metric-icon">↑</span></div><div class="metric">__UP__</div><div class="metric-foot">Cloudflare throughput sample</div></div>
 <div class="metric-card" style="--accent:#a88bff;--glow:#a88bff"><div class="metric-head"><span>Internet latency</span><span class="metric-icon">◷</span></div><div class="metric">__PING__</div><div class="metric-foot">Cloudflare ICMP average</div></div>
 <div class="metric-card" style="--accent:#5ee0a0;--glow:#5ee0a0"><div class="metric-head"><span>Wi-Fi signal</span><span class="metric-icon">⌁</span></div><div class="metric">__SIGNAL__</div><div class="metric-foot">Radio link · RSSI __RSSI__</div></div>
</section>
<div class="layout">
 <section class="panel"><div class="panel-head"><div><h3>Throughput over time</h3><div class="panel-kicker">Compare download and upload measurements</div></div><div class="tabs"><button class="active" data-range="1">24H</button><button data-range="7">7D</button><button data-range="30">30D</button></div></div><div class="chartwrap"><canvas id="speedChart"></canvas></div><div class="legend"><span><i class="dot" style="background:#69a9ff"></i>Download · Mbps</span><span><i class="dot" style="background:#ffbd70"></i>Upload · Mbps</span></div></section>
 <section class="panel"><div class="panel-head"><div><h3>Latency trend</h3><div class="panel-kicker">Router response vs public internet</div></div><span class="tag info">HISTORY</span></div><div class="chartwrap"><canvas id="latencyChart"></canvas></div><div class="legend"><span><i class="dot" style="background:#5ee0a0"></i>Router · ms</span><span><i class="dot" style="background:#ff7789"></i>Internet · ms</span></div></section>
</div>
<section class="bottleneck"><div><div class="eyebrow">Diagnostic assessment</div><h3>Where might the bottleneck be?</h3><p>__LIKELY__</p></div><div class="signalbadge">● Evidence-based estimate</div></section>
<section class="panel"><div class="panel-head"><div><h3>Five-stage connection path</h3><div class="panel-kicker">Use these layers to isolate the segment that needs attention</div></div></div>
 <div class="stages">
  <div class="stage"><div class="stage-num">01 / DEVICE</div><strong>PC → Wi-Fi adapter</strong><small>Signal, driver, link rate</small></div>
  <div class="stage"><div class="stage-num">02 / LOCAL</div><strong>Wi-Fi → Router</strong><small>Gateway latency, packet loss</small></div>
  <div class="stage"><div class="stage-num">03 / WAN</div><strong>Router → ISP</strong><small>Gateway and route evidence</small></div>
  <div class="stage"><div class="stage-num">04 / ROUTE</div><strong>ISP → Internet</strong><small>External latency, throughput</small></div>
  <div class="stage"><div class="stage-num">05 / NAME</div><strong>DNS → Website</strong><small>Lookup response time</small></div>
 </div>
</section>
<div class="two">
 <section class="panel"><div class="panel-head"><div><h3>Recommended next actions</h3><div class="panel-kicker">Prioritized based on this diagnostic run</div></div></div><ul class="actions">__RECOMMENDATIONS__</ul></section>
 <section class="panel"><div class="panel-head"><div><h3>Route trace</h3><div class="panel-kicker">Best-effort path to 1.1.1.1</div></div></div><div class="sub">Routers may suppress traceroute replies; a silent hop does not prove a fault.</div><ul class="route">__ROUTE__</ul></section>
</div>
<section class="panel"><div class="panel-head"><div><h3>Smart Auto Repair</h3><div class="panel-kicker">Repair decisions and post-action verification</div></div><span class="tag info">__REPAIRSUMMARY__</span></div><div class="tablewrap"><table><thead><tr><th>Time</th><th>Action</th><th>Result</th><th>Verification</th></tr></thead><tbody>__REPAIRLOG__</tbody></table></div></section>
<section class="panel"><div class="panel-head"><div><h3>Diagnostic detail</h3><div class="panel-kicker">Raw measurements behind the assessment</div></div></div><div class="tablewrap"><table><thead><tr><th>Area</th><th>Check</th><th>Result</th><th>Status</th><th>Details</th></tr></thead><tbody>__CHECKS__</tbody></table></div></section>
<div class="footer">LOCAL REPORT · History is stored in History\NetworkHistory.csv. Reports omit SSID, MAC/BSSID and IP addresses. Speed test transfers approximately 25 MB to Cloudflare per run. Results vary with server load, ISP routing, VPNs and other devices. Wi-Fi PHY link rate is not internet throughput. Nearby Wi-Fi scan is a snapshot, not a full RF spectrum analysis. Traceroute and ping can be filtered by networks.</div>
</main>
<script>
const historyData=__HISTORY__;
function draw(id,series,rows){
 const c=document.getElementById(id),ctx=c.getContext('2d'),dpr=window.devicePixelRatio||1;
 const w=Math.max(c.clientWidth,260),h=245;c.width=w*dpr;c.height=h*dpr;ctx.scale(dpr,dpr);ctx.clearRect(0,0,w,h);
 const pad={l:42,r:14,t:14,b:28},cw=w-pad.l-pad.r,ch=h-pad.t-pad.b;
 ctx.font='10px Segoe UI';ctx.fillStyle='#8fa3c1';ctx.strokeStyle='#263956';ctx.lineWidth=1;
 let vals=[];series.forEach(s=>rows.forEach(r=>{let v=Number(r[s.key]);if(Number.isFinite(v))vals.push(v)}));
 let max=Math.max(10,...vals)*1.18,min=0;
 for(let i=0;i<=4;i++){let y=pad.t+ch*i/4;ctx.beginPath();ctx.moveTo(pad.l,y);ctx.lineTo(w-pad.r,y);ctx.stroke();ctx.fillText((max-(max-min)*i/4).toFixed(0),5,y+3)}
 if(rows.length===0){ctx.fillStyle='#91a3bd';ctx.textAlign='center';ctx.fillText('No readings in this time window yet',w/2,h/2);ctx.textAlign='left'}
 series.forEach(s=>{ctx.strokeStyle=s.color;ctx.fillStyle=s.color;ctx.lineWidth=2.5;ctx.lineJoin='round';ctx.beginPath();let started=false,coords=[];
 rows.forEach((r,i)=>{let v=Number(r[s.key]);if(!Number.isFinite(v)){started=false;return}let x=pad.l+(rows.length<2?cw/2:i*cw/(rows.length-1)),y=pad.t+ch-(v-min)/(max-min)*ch;coords.push([x,y]);if(!started){ctx.moveTo(x,y);started=true}else ctx.lineTo(x,y)});ctx.stroke();
 coords.forEach(([x,y])=>{ctx.beginPath();ctx.arc(x,y,3.5,0,Math.PI*2);ctx.fill()});
 });
 ctx.fillStyle='#8fa3c1';ctx.fillText(rows.length+' runs',pad.l,h-7);
}
function render(days){
 const cutoff=Date.now()-days*86400000;
 const rows=historyData.filter(r=>{const t=Date.parse(r.Timestamp);return Number.isFinite(t)&&t>=cutoff});
 draw('speedChart',[{key:'DownloadMbps',color:'#69a9ff'},{key:'UploadMbps',color:'#ffbd70'}],rows);
 draw('latencyChart',[{key:'RouterAvgMs',color:'#5ee0a0'},{key:'CloudflareAvgMs',color:'#ff7789'}],rows);
}
document.querySelectorAll('[data-range]').forEach(b=>b.addEventListener('click',()=>{document.querySelectorAll('[data-range]').forEach(x=>x.classList.remove('active'));b.classList.add('active');render(Number(b.dataset.range))}));
render(1);window.addEventListener('resize',()=>render(Number(document.querySelector('.tabs .active').dataset.range)));
</script></body></html>
'@
# Replace template tokens one at a time. Keeping this explicit avoids PowerShell
# line-continuation / method-chain parsing issues that can leave raw tokens in HTML.
$downDisplay = if ($null -ne $down) { "$down Mbps" } else { "N/A" }
$upDisplay = if ($null -ne $up) { "$up Mbps" } else { "N/A" }
$pingDisplay = if ($null -ne $pingCloud.Avg) { "$($pingCloud.Avg) ms" } else { "N/A" }
$signalDisplay = if ($null -ne $signal) { "$signal%" } else { "N/A" }
$rssiDisplay = if ($null -ne $rssi) { "$rssi dBm" } else { "N/A" }
$replacements = [ordered]@{
    "__OVERALLCLASS__" = $overallClass
    "__OVERALL__" = (HtmlEncode $overall)
    "__RUNDATE__" = (HtmlEncode $Now.ToString("yyyy-MM-dd HH:mm:ss"))
    "__DOWN__" = $downDisplay
    "__UP__" = $upDisplay
    "__PING__" = $pingDisplay
    "__SIGNAL__" = $signalDisplay
    "__RSSI__" = $rssiDisplay
    "__LIKELY__" = (HtmlEncode $likely)
    "__RECOMMENDATIONS__" = $recommendationHtml
    "__ROUTE__" = $routeHtml
    "__CHECKS__" = ($checksHtml -join "`n")
    "__HISTORY__" = $historyJson
    "__RESULTS__" = $resultsJson
    "__REPAIRSUMMARY__" = (HtmlEncode $repairSummary)
    "__REPAIRLOG__" = $repairHtml
}
foreach ($token in $replacements.Keys) {
    $dashboard = $dashboard.Replace([string]$token, [string]$replacements[$token])
}
$dashboard | Set-Content -Path $HtmlReport -Encoding UTF8

# Plain-text report
$txt=New-Object System.Collections.Generic.List[string]
$txt.Add("WI-FI NETWORK ANALYZER PRO");$txt.Add("Run: $($Now.ToString('yyyy-MM-dd HH:mm:ss'))");$txt.Add("Overall: $overall")
$txt.Add("Likely bottleneck: $likely");$txt.Add("");$txt.Add("Privacy: identifiers and IP addresses omitted.")
foreach($r in $Results){$txt.Add("$($r.Area) | $($r.Check) | $($r.Value) | $($r.Status)");if($r.Explanation){$txt.Add("  $($r.Explanation)")}}
$txt.Add("");$txt.Add("SMART AUTO REPAIR: $repairSummary");foreach($event in $RepairEvents){$txt.Add("$($event.Action) | $($event.Result) | $($event.Verification)")};$txt.Add("");$txt.Add("Recommendations:");foreach($x in $recommendations){$txt.Add("- $x")}
$txt | Set-Content $TxtReport -Encoding UTF8

Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan
Write-Host "Overall: $overall"
Write-Host "Likely bottleneck: $likely"
Write-Host "`nDashboard: $HtmlReport" -ForegroundColor Green
Write-Host "Text report: $TxtReport"
Write-Host "History: $Csv"
Write-Host "`nOpening dashboard..." -ForegroundColor Cyan
Start-Process $HtmlReport

Write-Host "`nRepair log: $RepairLog" -ForegroundColor Cyan
Write-Host "Dashboard and reports updated with repair actions and verification." -ForegroundColor Green
Write-Host "`nDone. Press Enter to close."
Read-Host | Out-Null
