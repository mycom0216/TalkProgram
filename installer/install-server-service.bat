@echo off
setlocal
set "SERVICE_EXE=%~dp0ChatApp.Service.exe"
if not exist "%SERVICE_EXE%" (
  echo Publish ChatApp.Service and place the deployment folder contents here first.
  exit /b 1
)
sc.exe create "ChatApp Server" binPath= "\"%SERVICE_EXE%\"" start= auto DisplayName= "ChatApp Server"
if errorlevel 1 exit /b %errorlevel%
sc.exe failure "ChatApp Server" reset= 86400 actions= restart/5000/restart/5000/restart/5000
sc.exe start "ChatApp Server"
endlocal
