# Imagen única: compila la interfaz (Node), publica la API (.NET) y ejecuta todo en un contenedor Linux.
# NOTA: este Dockerfile no pudo construirse en el entorno de desarrollo (sin Docker); cada etapa equivale a comandos que sí se ejecutaron.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npx vite build --outDir /web/dist --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY server/Pos.sln ./
COPY server/src/Pos.Api/Pos.Api.csproj src/Pos.Api/
COPY server/tests/Pos.Tests/Pos.Tests.csproj tests/Pos.Tests/
RUN dotnet restore src/Pos.Api/Pos.Api.csproj
COPY server/src/ src/
RUN dotnet publish src/Pos.Api/Pos.Api.csproj -c Release --no-restore -o /out
COPY --from=web /web/dist /out/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# pg_dump / pg_restore para los respaldos
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api /out ./
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Backup__PgBinDir=/usr/bin \
    Backup__Directory=/data/backups \
    TZ=America/Santo_Domingo
EXPOSE 8080
# Las plataformas inyectan PORT; si no, 8080.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet Pos.Api.dll"]
