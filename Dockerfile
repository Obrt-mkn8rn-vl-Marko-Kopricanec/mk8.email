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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    MK8EMAIL_CONFIG_FILE=/run/secrets/mk8email_config

RUN install -d -o app -g app -m 0700 \
        /var/lib/mk8email-admin/data-protection \
        /var/log/mk8email-admin

COPY --from=build --chown=root:root /app/cli ./cli
COPY --from=build --chown=root:root /app/admin ./admin

USER app

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=12s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/cli/mk8.email.Application.CLI.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "/app/admin/mk8.email.Gateway.dll"]
