# ========================================================
# Stage 1: Build & Publish
# ========================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copie des fichiers de configuration et projets pour la mise en cache des layers NuGet
COPY CodinGame.sln Directory.Build.props ./
COPY src/CodinGame.AppHost/CodinGame.AppHost.csproj src/CodinGame.AppHost/
COPY src/CodinGame.ServiceDefaults/CodinGame.ServiceDefaults.csproj src/CodinGame.ServiceDefaults/
COPY src/CodinGame.Core/CodinGame.Core.csproj src/CodinGame.Core/
COPY src/CodinGame.Agents/CodinGame.Agents.csproj src/CodinGame.Agents/
COPY src/CodinGame.Cli/CodinGame.Cli.csproj src/CodinGame.Cli/

RUN dotnet restore CodinGame.sln

# Copie de tout le code source
COPY . .

# Publication des exécutables
RUN dotnet publish src/CodinGame.AppHost/CodinGame.AppHost.csproj -c Release -o /app/apphost
RUN dotnet publish src/CodinGame.Cli/CodinGame.Cli.csproj -c Release -o /app/cli

# ========================================================
# Stage 2: Runtime Linux (Ubuntu-based ASP.NET 10)
# ========================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Installation des dépendances et compilateurs pour exécuter les bots CodinGame
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    ca-certificates \
    build-essential \
    python3 \
    && rm -rf /var/lib/apt/lists/*

# Copie des binaires compilés
COPY --from=build /app/apphost ./apphost
COPY --from=build /app/cli ./cli

# Dossier pour les données persistantes (matches.db, replays, logs)
RUN mkdir -p /app/data

# Exposition du port Dashboard Aspire
EXPOSE 15031
EXPOSE 19144
EXPOSE 20290

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPIRE_ALLOW_UNSECURED_TRANSPORT=true

# Point d'entrée par défaut : lance l'orchestrateur Aspire
ENTRYPOINT ["dotnet", "./apphost/CodinGame.AppHost.dll"]
