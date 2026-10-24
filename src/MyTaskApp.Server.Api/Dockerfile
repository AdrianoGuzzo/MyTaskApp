# O servidor de sincronizacao (ADR-061). Build a partir da raiz do repositorio:
#   docker build -f src/MyTaskApp.Server.Api/Dockerfile -t mytaskapp-sync .
# Segredos so por variavel de ambiente (docs/sync-server.md); nada e copiado para a imagem.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MyTaskApp.Sync.Contracts/MyTaskApp.Sync.Contracts.csproj src/MyTaskApp.Sync.Contracts/
COPY src/MyTaskApp.Remote.Contracts/MyTaskApp.Remote.Contracts.csproj src/MyTaskApp.Remote.Contracts/
COPY src/MyTaskApp.Server.Application/MyTaskApp.Server.Application.csproj src/MyTaskApp.Server.Application/
COPY src/MyTaskApp.Server.Infrastructure/MyTaskApp.Server.Infrastructure.csproj src/MyTaskApp.Server.Infrastructure/
COPY src/MyTaskApp.Server.Api/MyTaskApp.Server.Api.csproj src/MyTaskApp.Server.Api/
RUN dotnet restore src/MyTaskApp.Server.Api/MyTaskApp.Server.Api.csproj

COPY src/MyTaskApp.Sync.Contracts/ src/MyTaskApp.Sync.Contracts/
COPY src/MyTaskApp.Remote.Contracts/ src/MyTaskApp.Remote.Contracts/
COPY src/MyTaskApp.Server.Application/ src/MyTaskApp.Server.Application/
COPY src/MyTaskApp.Server.Infrastructure/ src/MyTaskApp.Server.Infrastructure/
COPY src/MyTaskApp.Server.Api/ src/MyTaskApp.Server.Api/
RUN dotnet publish src/MyTaskApp.Server.Api/MyTaskApp.Server.Api.csproj -c Release --no-restore -o /app

# O bundle das migrations: aplicado antes de subir uma versao nova (docs/sync-server.md).
RUN dotnet tool install --global dotnet-ef --version 10.0.12 \
    && /root/.dotnet/tools/dotnet-ef migrations bundle \
        -p src/MyTaskApp.Server.Infrastructure -s src/MyTaskApp.Server.Infrastructure \
        -o /app/efbundle --self-contained -r linux-x64

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "MyTaskApp.Server.Api.dll"]
