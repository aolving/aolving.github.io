# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the layer is cached until a project file changes.
COPY ToolShed.sln ./
COPY src/ToolShed.Contracts/ToolShed.Contracts.csproj src/ToolShed.Contracts/
COPY src/ToolShed.Client/ToolShed.Client.csproj src/ToolShed.Client/
COPY src/ToolShed.Web/ToolShed.Web.csproj src/ToolShed.Web/
COPY tests/ToolShed.Tests/ToolShed.Tests.csproj tests/ToolShed.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/ToolShed.Web -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# The database, the photos and the signing keys all live here. Mount a volume on it.
RUN mkdir -p /app/App_Data && chown -R app:app /app/App_Data
VOLUME /app/App_Data

# The container speaks plain HTTP and expects a TLS-terminating proxy (see docker-compose.yml)
# in front of it, so it trusts X-Forwarded-For/Proto. Never publish this port to the internet.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Hosting__BehindProxy=true

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ToolShed.Web.dll"]
