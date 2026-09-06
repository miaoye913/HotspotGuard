@echo off
chcp 65001 >nul
rem ============================================================
rem 卸载: 删除"屏幕触发热点"计划任务
rem 用法: 双击本文件即可
rem ============================================================
schtasks /Delete /TN "HotspotGuard" /F 2>nul
schtasks /Delete /TN "AutoHotspotOnScreen" /F 2>nul
echo [完成] 已删除计划任务 HotspotGuard / AutoHotspotOnScreen。
pause
