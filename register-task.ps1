# register-task.ps1 - 注册"屏幕/USB 触发热点"开机自启动计划任务(需管理员运行)
# 直接运行 HotspotGuard.exe (winexe, 无控制台窗口)
$Out = 'D:\HotspotGuard\task-register-result.txt'
Set-Content -Path $Out -Value '' -Encoding UTF8
function Log($m) { Add-Content -Path $Out -Value $m -Encoding UTF8; Write-Host $m }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log ("elevated: " + $isAdmin)

$cmd = 'schtasks /Create /F /TN "HotspotGuard" /TR "D:\HotspotGuard\HotspotGuard.exe watch" /SC ONLOGON /RL HIGHEST'
Log ("cmd: " + $cmd)
cmd /c $cmd 2>&1 | ForEach-Object { Log "  $_" }
Log ("rc: " + $LASTEXITCODE)

Log '--- verify ---'
schtasks /Query /TN "HotspotGuard" /V /FO LIST 2>&1 | Select-String -Pattern 'TaskName|Task To Run|计划任务状态|要运行的任务|计划类型|作为用户运行|上次运行|上次结果' | ForEach-Object { Log ("  " + $_.Line) }
Log '== DONE =='
