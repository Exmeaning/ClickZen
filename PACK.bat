@echo off
chcp 65001 >nul
echo ========================================
echo   启动 打包小程序
echo ========================================

REM 切换到项目根目录（重要！）
cd /d "C:\Python_Project\ControlMyPhone"

REM 激活 Conda 虚拟环境
call "C:\Users\admin\miniconda3\Scripts\activate.bat" ControlMyPhone

REM 检查虚拟环境是否激活成功
if "%CONDA_DEFAULT_ENV%"=="ControlMyPhone" (
    echo [信息] 虚拟环境已成功激活: %CONDA_DEFAULT_ENV%
) else (
    echo [错误] 虚拟环境激活失败！
    pause
    exit /b 1
)

echo [信息] 工作目录: %CD%
echo.

REM 设置环境变量（可选）
set ENVIRONMENT=prod

REM 使用当前环境中的 Python 运行构建脚本
python build_nuitka.py

REM 检查退出码
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [错误] 打包过程异常退出！
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [信息] 打包完成！
pause