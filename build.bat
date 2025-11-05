@echo off
chcp 65001 >nul
echo ========================================
echo ClickZen 打包脚本
echo ========================================
echo.
REM 切换到项目根目录（重要！）
cd /d C:\Python_Project\ControlMyPhone

REM 激活虚拟环境
call venv\Scripts\activate.bat

REM 检查虚拟环境
if "%VIRTUAL_ENV%"=="" (
    echo [错误] 虚拟环境激活失败！
    pause
    exit /b 1
)

echo [信息] 虚拟环境: %VIRTUAL_ENV%
echo [信息] 工作目录: %CD%
echo.


:: 安装/更新依赖
echo [1/5] 安装依赖...
pip install -r requirements.txt --quiet
pip install nuitka --quiet

:: 清理旧的构建
echo [2/5] 清理旧版本...
if exist dist rmdir /s /q dist
if exist build rmdir /s /q build
if exist ClickZen.exe del ClickZen.exe

:: 创建版本信息文件
echo [3/5] 生成版本信息...
echo # -*- coding: utf-8 -*- > version_info.py
echo VERSION_INFO = { >> version_info.py
echo     'CompanyName': 'ClickZen', >> version_info.py
echo     'FileDescription': 'Android Automation Tool', >> version_info.py
echo     'FileVersion': '1.0.0', >> version_info.py
echo     'InternalName': 'ClickZen', >> version_info.py
echo     'LegalCopyright': 'MIT License', >> version_info.py
echo     'OriginalFilename': 'ClickZen.exe', >> version_info.py
echo     'ProductName': 'ClickZen', >> version_info.py
echo     'ProductVersion': '1.0.0' >> version_info.py
echo } >> version_info.py

:: 使用Nuitka编译
echo [4/5] 编译程序...
python -m nuitka ^
    --standalone ^
    --onefile ^
    --windows-disable-console ^
    --windows-icon-from-ico=icon.ico ^
    --enable-plugin=pyqt5 ^
    --include-qt-plugins=sensible,styles ^
    --output-dir=dist ^
    --windows-company-name="ClickZen" ^
    --windows-product-name="ClickZen" ^
    --windows-file-version="1.0.0.0" ^
    --windows-product-version="1.0.0.0" ^
    --windows-file-description="Android Automation Tool" ^
    --assume-yes-for-downloads ^
    --show-progress ^
    --show-memory ^
    main.py

:: 检查编译结果
if not exist dist\main.exe (
    echo [错误] 编译失败！
    pause
    exit /b 1
)

:: 重命名输出文件
echo [5/5] 整理文件...
move dist\main.exe ClickZen.exe >nul

:: 清理临时文件
del version_info.py >nul 2>&1
rmdir /s /q build >nul 2>&1
rmdir /s /q dist >nul 2>&1
rmdir /s /q main.dist >nul 2>&1
rmdir /s /q main.build >nul 2>&1

echo.
echo ========================================
echo 打包完成！
echo 输出文件: ClickZen.exe
echo ========================================
pause