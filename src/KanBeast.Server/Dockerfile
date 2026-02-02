FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/KanBeast.Server/KanBeast.Server.csproj", "src/KanBeast.Server/"]
RUN dotnet restore "src/KanBeast.Server/KanBeast.Server.csproj"
COPY . .
WORKDIR "/src/src/KanBeast.Server"
RUN dotnet build "KanBeast.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "KanBeast.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "KanBeast.Server.dll"]
