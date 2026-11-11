# syntax=docker/dockerfile:1

# ---- Compilazione -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-resolute AS build
WORKDIR /src
# Solo le librerie native dell'architettura dell'immagine: senza, la pubblicazione includeva SQLite
# per circa 25 piattaforme (~34 MB). TARGETARCH lo imposta Docker (amd64 o arm64).
ARG TARGETARCH

# Prima solo i file di progetto: il restore resta in cache finché non cambiano le dipendenze.
COPY global.json Launcher.slnx ./
COPY src/Launcher.Server/Launcher.Server.csproj src/Launcher.Server/
RUN dotnet restore src/Launcher.Server/Launcher.Server.csproj \
        --runtime linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)

COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Launcher.Server/Launcher.Server.csproj \
        --configuration Release \
        --no-restore \
        --runtime linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
        --self-contained false \
        --output /app \
        -p:UseAppHost=false \
        -p:DebugType=none \
    && mkdir -p /data-empty

# ---- Esecuzione -------------------------------------------------------------
# Immagine "chiseled": niente shell, niente package manager, utente non root (app, uid 1654).
# La variante "extra" include ICU e fusi orari (date in Europe/Rome, ricerca senza accenti).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-resolute-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data-empty /data
# Launcher per Windows della stessa versione (messo qui dalla pipeline), in sola lettura per il portale.
COPY launcher-release/ ./launcher/

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    Launcher__DataDirectory=/data

USER 1654
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "Launcher.Server.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Launcher.Server.dll"]
