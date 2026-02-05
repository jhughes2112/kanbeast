@echo off
setlocal

set IMAGE_NAME=kanbeast
set CONTAINER_NAME=kanbeast-server
set NETWORK_NAME=kanbeast-network

docker build -t %IMAGE_NAME% -f Dockerfile .
if errorlevel 1 exit /b 1

docker rm -f %CONTAINER_NAME% >nul 2>&1
docker network rm kanbeast-network >nul 2>&1
docker network create %NETWORK_NAME%

start http://localhost:8080

docker run --rm -it ^
  --name %CONTAINER_NAME% ^
  --network %NETWORK_NAME% ^
  -p 8080:8080 ^
  -e ASPNETCORE_ENVIRONMENT=Production ^
  -e ASPNETCORE_URLS=http://+:8080 ^
  -v "%cd%"/env:/workspace ^
  -v //var/run/docker.sock:/var/run/docker.sock ^
  %IMAGE_NAME%
