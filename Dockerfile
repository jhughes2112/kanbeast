# -----------------------------
# Build stage
# -----------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
USER root
WORKDIR /src
ARG BUILD_CONFIGURATION=Release

# Copy project files first for better layer caching
COPY ["Entrypoint/Entrypoint.csproj", "Entrypoint/"]
COPY ["KanBeast.Server/KanBeast.Server.csproj", "KanBeast.Server/"]
COPY ["KanBeast.Shared/KanBeast.Shared.csproj", "KanBeast.Shared/"]
COPY ["KanBeast.Worker/KanBeast.Worker.csproj", "KanBeast.Worker/"]

RUN dotnet restore "Entrypoint/Entrypoint.csproj"

# Copy everything else
COPY . .

WORKDIR /src/Entrypoint
RUN dotnet publish "Entrypoint.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    /p:UseAppHost=false


# -----------------------------
# Runtime stage (full tooling)
# -----------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final
USER root
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_ENVIRONMENT=Development
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_CONTENTROOT=/workspace

# Install required system tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    git git-lfs build-essential cmake make gcc g++ \
    python3 python3-pip python3-venv \
    curl ca-certificates gnupg wget unzip zip jq ripgrep tree \
    htop vim nano less openssh-client sudo \
    netcat-openbsd dnsutils iputils-ping \
    postgresql-client default-mysql-client sqlite3 \
    libssl-dev libffi-dev pkg-config \
    && rm -rf /var/lib/apt/lists/*

# Install Node.js LTS (22.x)
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

# Install common global npm packages
RUN npm install -g \
    typescript \
    ts-node \
    yarn \
    pnpm \
    eslint \
    prettier

# Install common Python packages
RUN pip3 install --break-system-packages --no-cache-dir \
    pytest \
    black \
    flake8 \
    mypy \
    requests \
    pyyaml

# Configure git defaults
RUN git config --global init.defaultBranch main \
    && git config --global core.autocrlf input \
    && git config --global pull.rebase true

# Inject Visual Studio debugging script to /root/
RUN mkdir /root/.vs-debugger && curl -sSL https://aka.ms/getvsdbgsh -o '/root/.vs-debugger/GetVsDbg.sh'

# Copy published application
COPY --from=build /app/publish .

# Optional workspace content
COPY env /workspace

WORKDIR /workspace

ENTRYPOINT ["dotnet", "/app/Entrypoint.dll"]