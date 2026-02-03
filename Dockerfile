FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 8080
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
COPY ["KanBeast.Server/KanBeast.Server.csproj", "/KanBeast.Server/"]
COPY ["KanBeast.Worker/KanBeast.Worker.csproj", "/KanBeast.Worker/"]
RUN dotnet restore "/KanBeast.Server/KanBeast.Server.csproj"
RUN dotnet restore "/KanBeast.Worker/KanBeast.Worker.csproj"
COPY . .
RUN dotnet publish "/KanBeast.Server/KanBeast.Server.csproj" -c Release -o /app/publish/server /p:UseAppHost=false
RUN dotnet publish "/KanBeast.Worker/KanBeast.Worker.csproj" -c Release -o /app/publish/worker /p:UseAppHost=false

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish/server ./server
COPY --from=build /app/publish/worker ./worker
COPY env ./env
ENTRYPOINT ["dotnet", "/app/server/KanBeast.Server.dll"]
