@echo off
chcp 65001 >nul
if not exist "D:\项目归档\Differ\开发预览\Differ.Phase1.Preview.exe" (
  echo 开发预览不存在，请查看 docs\PROJECT_STATUS.md 中的构建说明。
  pause
  exit /b 1
)
start "" "D:\项目归档\Differ\开发预览\Differ.Phase1.Preview.exe"
