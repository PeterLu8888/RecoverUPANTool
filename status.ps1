# Live status for a running RawImager recovery job.
# Usage:  .\status.ps1                 # refresh every 5 s until the imager exits
#         .\status.ps1 -Seconds 60     # run for 60 s then stop watching
#         .\status.ps1 -Interval 2     # refresh faster
param(
    [string]$Dir      = 'D:\PlayGround\backup',
    [int]$Interval    = 5,
    [int]$Seconds     = 0
)

$ErrorActionPreference = 'SilentlyContinue'

$logPath = Join-Path $Dir 'image-progress.log'
$mapPath = Join-Path $Dir 'bad-sectors.txt'

function Read-MapLines {
    Get-Content -LiteralPath $mapPath -ErrorAction SilentlyContinue
}

# --- one-time: complete set of 4 MiB chunks that failed in pass 1 ---------------
$base = New-Object 'System.Collections.Generic.HashSet[long]'
foreach ($ln in (Read-MapLines)) {
    $p = $ln.Split(' ')
    if ($p.Length -lt 2) { continue }
    if ($p[0] -eq 'BAD4M') { [void]$base.Add([long]$p[1]) }
    elseif ($p[0] -eq 'BADRUN') {
        $s = [long]$p[1]; $e = $s + [long]$p[2]
        for ($o = $s; $o -lt $e; $o += 4MB) { [void]$base.Add($o) }
    }
}

$history = New-Object System.Collections.Generic.List[object]
$t0 = Get-Date

while ($true) {
    $now  = Get-Date
    $proc = Get-Process RawImager -ErrorAction SilentlyContinue | Select-Object -First 1

    $mapLines = Read-MapLines
    $rec = @($mapLines | Where-Object { $_.StartsWith('RECOVERED ') }).Count

    $history.Add([pscustomobject]@{ T = $now; Rec = $rec })
    while ($history.Count -gt 2 -and ($now - $history[0].T).TotalSeconds -gt 30) { $history.RemoveAt(0) }
    $span = ($now - $history[0].T).TotalSeconds
    $rate = if ($span -gt 1) { ($rec - $history[0].Rec) / $span } else { 0 }

    $tail = Get-Content -LiteralPath $logPath -Tail 300 -ErrorAction SilentlyContinue
    $lastLine = ($tail | Where-Object { $_.Trim() } | Select-Object -Last 1)
    $lastProg = ($tail | Where-Object { $_ -match 'pos=' } | Select-Object -Last 1)
    $roundLine = ($tail | Where-Object { $_ -match 'round \d+: attempted=' } | Select-Object -Last 1)

    $phase = '第一遍：顺序扫描'
    $markers = @()
    for ($i = 0; $i -lt $tail.Count; $i++) {
        $l = $tail[$i]
        if ($l -match 'pre-wait')                          { $markers += ,@($i, '第二遍：静置预热') }
        elseif ($l -match 'cool-down')                     { $markers += ,@($i, '第二遍：冷却等待') }
        elseif ($l -match 'round \d+: attempted=')         { $markers += ,@($i, '第二遍：重试') }
        elseif ($l -match 'splitting')                     { $markers += ,@($i, '第三遍：拆块抢救') }
        elseif ($l -match 'pass1 complete')                { $markers += ,@($i, '第二遍：待开始') }
    }
    if ($markers.Count -gt 0) { $phase = $markers[-1][1] }
    if (-not $proc) { $phase = '已结束' }

    $pos = $null; $written = $null; $zero = $null
    if ($lastProg -and $lastProg -match 'pos=([\d,\.]+) GiB.*written=([\d,\.]+) GiB zeroSkip=([\d,\.]+) GiB') {
        $pos = $matches[1]; $written = $matches[2]; $zero = $matches[3]
    }
    $free = (Get-Volume -DriveLetter D).SizeRemaining / 1GB
    $pending = if ($rec -lt $base.Count) { $base.Count - $rec } else { 0 }

    "==================== {0:HH:mm:ss} ====================" -f $now
    if ($proc) {
        "进程   : 运行中  PID {0}  CPU {1:N0}s  内存 {2:N0} MB" -f $proc.Id, $proc.CPU, ($proc.WorkingSet64 / 1MB)
    } else {
        "进程   : 已退出"
    }
    "阶段   : {0}" -f $phase
    if ($pos) { "已扫   : {0} GiB / 1,863.01 GiB    镜像: {1} 数据 + {2} 零洞" -f $pos, $written, $zero }
    "坏块   : {0} / {1} 待解决    已抢回 {2} 块（{3:N1} 块/秒，约 {4:N2} GiB）" -f $pending, $base.Count, $rec, $rate, ($rec * 4MB / 1GB)
    "D: 剩余: {0:N1} GiB" -f $free
    if ($roundLine) { "本轮进度: {0}" -f $roundLine.Trim() }
    "最近日志: {0}" -f $lastLine
    ""

    if ($Seconds -gt 0 -and ($now - $t0).TotalSeconds -ge $Seconds) { break }
    if (-not $proc -and $Seconds -eq 0) { break }
    Start-Sleep -Seconds $Interval
}
