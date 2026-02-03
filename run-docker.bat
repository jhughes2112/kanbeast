@echo off
setlocal

set IMAGE_NAME=kanbeast
set CONTAINER_NAME=kanbeast-server
set NETWORK_NAME=kanbeast-network

docker network create %NETWORK_NAME%

docker build -t %IMAGE_NAME% -f Dockerfile .
if errorlevel 1 exit /b 1

docker rm -f %CONTAINER_NAME% >nul 2>&1

start http://localhost:8080

docker run --rm -it ^
  --name %CONTAINER_NAME% ^
  --network %NETWORK_NAME% ^
  -p 8080:8080 ^
  -e ASPNETCORE_ENVIRONMENT=Production ^
  -e ASPNETCORE_URLS=http://+:8080 ^
  -v "%cd%"/env:/app/env ^
  %IMAGE_NAME% --WorkerImage %IMAGE_NAME% --DockerNetwork %NETWORK_NAME% --ServerUrl http://%CONTAINER_NAME%:8080
