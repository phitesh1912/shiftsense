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
RUN useradd --uid 10001 --create-home app && mkdir -p /data && chown -R app:app /data /app
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
    CMD curl -fsS "http://127.0.0.1:${PORT:-8080}/health" || exit 1
# Render (and most PaaS) inject PORT; default to 8080 locally.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet api/ShiftSense.Api.dll"]
