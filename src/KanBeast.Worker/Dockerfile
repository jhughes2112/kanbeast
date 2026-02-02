FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
WORKDIR /app

# Install git for repository operations
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/KanBeast.Worker/KanBeast.Worker.csproj", "src/KanBeast.Worker/"]
RUN dotnet restore "src/KanBeast.Worker/KanBeast.Worker.csproj"
COPY . .
WORKDIR "/src/src/KanBeast.Worker"
RUN dotnet build "KanBeast.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "KanBeast.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "KanBeast.Worker.dll"]
