@echo off
chcp 65001 >nul
rem ============================================================
rem 安装: 注册"屏幕触发热点"计划任务 (登录时托盘监控)
rem 用法: 双击本文件即可, 会自动请求管理员权限(UAC 点是)
rem ============================================================

rem --- 检查管理员权限, 不足则自动提权重启 ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 正在请求管理员权限...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set TASK=HotspotGuard
set EXE=D:\HotspotGuard\HotspotGuard.exe

rem 直接运行 exe (winexe, 无控制台窗口, 不会弹出蓝色 PowerShell)
schtasks /Create /F /TN "%TASK%" /TR "%EXE% watch" /SC ONLOGON /RL HIGHEST

if errorlevel 1 (
    echo.
    echo [失败] 注册任务出错, 请检查后重试。
    echo.
    pause
    exit /b 1
)

echo.
echo [成功] 任务 "%TASK%" 已注册: 登录触发 + 最高权限运行。
echo        登录后自动托盘监控:
echo          目标屏(DELF145)连接  -^> 自动开启移动热点并退出
echo          其他外接屏连接      -^> 不做任何操作, 直接退出
echo          运行满 5 分钟自动退出
echo.
echo 配置触发屏幕: hotspotguard.ps1 -Mode config  (或托盘图标右键 -^> 设置)
echo 运行日志    : D:\HotspotGuard\hotspotguard.log
echo 卸载        : 双击 uninstall-task.bat
echo.
echo 立即运行一次做验证(会先等 10 秒再判定)...
schtasks /Run /TN "%TASK%"
if errorlevel 1 echo [提示] 立即运行失败, 不影响下次登录自动执行。
echo.
pause
