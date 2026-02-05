# Build stage - compile .NET projects
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
COPY ["KanBeast.Server/KanBeast.Server.csproj", "/KanBeast.Server/"]
COPY ["KanBeast.Worker/KanBeast.Worker.csproj", "/KanBeast.Worker/"]
RUN dotnet restore "/KanBeast.Server/KanBeast.Server.csproj"
RUN dotnet restore "/KanBeast.Worker/KanBeast.Worker.csproj"
COPY . .
RUN dotnet publish "/KanBeast.Server/KanBeast.Server.csproj" -c Release -o /app/publish/server /p:UseAppHost=false
RUN dotnet publish "/KanBeast.Worker/KanBeast.Worker.csproj" -c Release -o /app/publish/worker /p:UseAppHost=false

# Runtime stage - full development environment for agents
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS final
WORKDIR /app
EXPOSE 8080

# Install essential build tools and utilities
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Version control
    git \
    git-lfs \
    # Build essentials
    build-essential \
    cmake \
    make \
    gcc \
    g++ \
    # Python
    python3 \
    python3-pip \
    python3-venv \
    # Node.js (via NodeSource for latest LTS)
    curl \
    ca-certificates \
    gnupg \
    # Utilities
    wget \
    unzip \
    zip \
    jq \
    tree \
    htop \
    vim \
    nano \
    less \
    openssh-client \
    sudo \
    # Network tools
    netcat-openbsd \
    dnsutils \
    iputils-ping \
    # Database clients
    postgresql-client \
    default-mysql-client \
    sqlite3 \
    # Development libraries
    libssl-dev \
    libffi-dev \
    pkg-config \
    && rm -rf /var/lib/apt/lists/*

# Install Node.js LTS (v22.x)
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

# Configure git for agent use
RUN git config --global init.defaultBranch main \
    && git config --global core.autocrlf input \
    && git config --global pull.rebase true

# Copy built applications
COPY --from=build /app/publish/server /app/server
COPY --from=build /app/publish/worker /app/worker
COPY --from=build /app/publish/server/appsettings.json ./appsettings.json
COPY env /workspace

ENV ASPNETCORE_CONTENTROOT=/workspace
WORKDIR /workspace
ENTRYPOINT ["dotnet", "/app/server/KanBeast.Server.dll"]
