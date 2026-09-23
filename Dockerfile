# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY . .
RUN dotnet restore mk8.email.slnx --locked-mode
RUN dotnet publish mk8.email.CLI/mk8.email.Application.CLI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/cli \
    --property:ContinuousIntegrationBuild=true \
    --property:UseAppHost=false
RUN dotnet publish mk8.email.Gateway/mk8.email.Gateway.csproj \
    --configuration Release \
    --no-restore \
    --output /app/admin \
    --property:ContinuousIntegrationBuild=true \
    --property:UseAppHost=false
RUN dotnet publish mk8.email.Application.Worker/mk8.email.Application.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/worker \
    --property:ContinuousIntegrationBuild=true \
    --property:UseAppHost=false
RUN dotnet publish mk8.email.Wake/mk8.email.Wake.csproj \
    --configuration Release \
    --no-restore \
    --output /app/wake \
    --property:ContinuousIntegrationBuild=true \
    --property:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.11-noble AS worker
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0 \
    MK8EMAIL_CONFIG_FILE=/run/secrets/worker_config

COPY --from=build --chown=root:root /app/worker ./worker
COPY --from=build --chown=root:root /app/wake ./wake
COPY --from=build --chown=root:root /app/cli ./cli
COPY --chown=root:root --chmod=0755 deploy/scripts/container-worker-entrypoint \
    /app/container-worker-entrypoint

USER app

HEALTHCHECK --interval=60s --timeout=20s --start-period=120s --retries=3 \
    CMD ["dotnet", "/app/wake/mk8.email.Wake.dll", "--probe-db", "/run/secrets/wake_database_connection"]

ENTRYPOINT ["/app/container-worker-entrypoint"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble AS gateway
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    MK8EMAIL_CONFIG_FILE=/run/secrets/gateway_config

RUN install -d -o app -g app -m 0700 \
        /var/lib/mk8email-admin/data-protection \
        /var/log/mk8email-admin

COPY --from=build --chown=root:root /app/cli ./cli
COPY --from=build --chown=root:root /app/admin ./admin

USER app

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=12s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/cli/mk8.email.Application.CLI.dll", "--healthcheck-gateway", "/run/secrets/gateway_config"]

ENTRYPOINT ["dotnet", "/app/admin/mk8.email.Gateway.dll"]
