# syntax=docker/dockerfile:1.4

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first to leverage layer caching
COPY trignis.sln ./
COPY Source/*.csproj ./Source/

# Restore dependencies (use BuildKit cache for NuGet packages)
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
	dotnet restore ./trignis.sln

# Copy the remaining source files
COPY . .

# Publish the Trignis project
WORKDIR /src/Source
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
	dotnet publish Trignis.csproj -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime
WORKDIR /app

# Install sqlite3 and curl for runtime healthchecks and basic tools
RUN apt-get update && apt-get install -y --no-install-recommends \
	sqlite3 \
	curl \
	&& rm -rf /var/lib/apt/lists/*

# Create directories used by the application
RUN mkdir -p /app/environments /app/log /app/exports

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production

# Copy published output
COPY --from=build /app/publish .

# Copy example environments and appsettings if present
COPY Source/environments /app/environments
COPY Source/appsettings.json /app/appsettings.json

# Ensure permissions
RUN chmod -R 755 /app

# Healthcheck
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:80/health || exit 1

EXPOSE 80

ENTRYPOINT ["dotnet", "Trignis.dll"]

