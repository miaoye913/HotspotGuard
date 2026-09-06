# list-monitors.ps1 - 列出电脑识别到的所有屏幕(历史连接过), 并标注当前是否连接
# 运行: powershell.exe -NoProfile -ExecutionPolicy Bypass -File list-monitors.ps1
# 原理: Windows 通过 EDID 记录每台连接过的显示器于注册表 HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY,
#       pnputil 枚举 Monitor 类设备即可读出(含已拔掉的)。

$raw = pnputil /enum-devices /class Monitor 2>$null
if (-not $raw) { Write-Host '未获取到设备列表'; exit 1 }

# 按 DISPLAY\ 实例行切成设备块(兼容提权后中文输出)
$blocks = @()
$current = @()
foreach ($line in $raw) {
    if ($line -match 'DISPLAY\\') {
        if ($current.Count -gt 0) { $blocks += ,($current -join "`n") }
        $current = @($line)
    }
    elseif ($current.Count -gt 0) { $current += $line }
}
if ($current.Count -gt 0) { $blocks += ,($current -join "`n") }

$rows = @()
foreach ($b in $blocks) {
    $inst  = if ($b -match '(?im)^\s*(?:Instance ID|实例 ID)\s*[:：]\s*(DISPLAY\\.+?)\s*$') { $Matches[1] } else { '' }
    $desc  = if ($b -match '(?im)^\s*(?:Device Description|设备描述)\s*[:：]\s*(.+?)\s*$') { $Matches[1] } else { '' }
    $stat  = if ($b -match '(?im)^\s*(?:Status|状态)\s*[:：]\s*(.+?)\s*$') { $Matches[1] } else { '' }

    $connected = ($stat -match 'Started|已启动')
    $pnp = ''
    if ($inst -match '^DISPLAY\\([^\\]+)\\') { $pnp = $Matches[1] }

    # 描述去本地化前缀(Generic PnP Monitor / 通用即插即用监视器 等)及外层括号
    $clean = $desc -replace '(?i)^Generic (PnP )?Monitor\s*', '' -replace '^通用(即插即用)?监视器\s*', '' -replace '^\((.+)\)$', '$1'

    $rows += [PSCustomObject]@{
        PnP_ID    = $pnp
        型号       = $(if ($clean) { $clean } else { '(未知/无型号信息)' })
        状态       = $(if ($connected) { '已连接' } else { '未连接(历史)' })
        实例       = $inst
    }
}

# 按 PnP ID 汇总: 型号取最完整的描述, 实例数, 是否任一实例当前已连接
$groups = $rows | Group-Object PnP_ID
$summary = foreach ($g in $groups) {
    $best = $g.Group | Sort-Object @{Expression = { $_.型号.Length }; Descending = $true } | Select-Object -First 1
    $anyConnected = $g.Group | Where-Object { $_.状态 -eq '已连接' }
    [PSCustomObject]@{
        PnP_ID   = $g.Name
        型号      = $best.型号
        状态      = $(if ($anyConnected) { '已连接' } else { '未连接(历史)' })
        实例数    = $g.Count
    }
}

Write-Host ''
Write-Host ('共识别到 ' + @($summary).Count + ' 款屏幕 (实例总数 ' + @($rows).Count + ')')
Write-Host '-----------------------------------------------------------'
$summary | Sort-Object 状态, 型号 | Format-Table -AutoSize | Out-String -Width 120 | Write-Host
Write-Host '说明:'
Write-Host '  1. "已连接" = 当前插着; "未连接(历史)" = 以前插过, Windows 仍记得。'
Write-Host '  2. 同一 PnP ID 有多个实例 = 这台显示器在不同接口/不同时间插过多次。'
Write-Host '  3. 实例中的 UID/端口段(如 UID256) 表示插在哪个显卡端口上。'
Write-Host '  4. 该记录只在"设备管理器里卸载该设备"时才会清除。'
