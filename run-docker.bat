@echo off
setlocal

set IMAGE_NAME=kanbeast
set CONTAINER_NAME=kanbeast-server
set NETWORK_NAME=kanbeast-network
set VOLUME_NAME=kanbeast-data

docker network inspect %NETWORK_NAME% >nul 2>&1 || docker network create %NETWORK_NAME% >nul
if errorlevel 1 exit /b 1

docker volume inspect %VOLUME_NAME% >nul 2>&1 || docker volume create %VOLUME_NAME% >nul
if errorlevel 1 exit /b 1

docker build -t %IMAGE_NAME% -f Dockerfile .
if errorlevel 1 exit /b 1

docker rm -f %CONTAINER_NAME% >nul 2>&1

docker run -d ^
  --name %CONTAINER_NAME% ^
  --network %NETWORK_NAME% ^
  -p 8080:8080 ^
  -e ASPNETCORE_ENVIRONMENT=Production ^
  -e ASPNETCORE_URLS=http://+:8080 ^
  -v %VOLUME_NAME%:/app/data ^
  %IMAGE_NAME%
if errorlevel 1 exit /b 1

echo Server running at http://localhost:8080
