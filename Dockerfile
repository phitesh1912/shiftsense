# ---- build ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ShiftSense.Api/ShiftSense.Api.csproj -c Release -o /app/api --nologo \
    && dotnet publish src/ShiftSense.Mcp/ShiftSense.Mcp.csproj -c Release -o /app/mcp --nologo

# ---- runtime -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV SHIFTSENSE_DB=/data/shiftsense.db \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app/api ./api
COPY --from=build /app/mcp ./mcp
# The aspnet base image already defines a non-root 'app' user (uid 1654); reuse it.
RUN mkdir -p /data && chown -R app:app /data /app
USER app
EXPOSE 8080
# No HEALTHCHECK instruction: the base image has no curl/wget, and the platform (Render) probes
# GET /health itself — see render.yaml.
# Render (and most PaaS) inject PORT; default to 8080 locally.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet api/ShiftSense.Api.dll"]
