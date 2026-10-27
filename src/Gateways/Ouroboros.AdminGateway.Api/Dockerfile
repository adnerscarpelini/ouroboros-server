FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG APP_VERSION=0.1.0
ARG SOURCE_REVISION=local
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ouroboros.Hosting/ src/Ouroboros.Hosting/
COPY src/Ouroboros.Observability/ src/Ouroboros.Observability/
COPY src/Gateways/Ouroboros.AdminGateway.Api/ src/Gateways/Ouroboros.AdminGateway.Api/
RUN dotnet publish src/Gateways/Ouroboros.AdminGateway.Api/Ouroboros.AdminGateway.Api.csproj -c Release -o /out /p:UseAppHost=false /p:Version=${APP_VERSION} /p:SourceRevisionId=${SOURCE_REVISION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24
ARG APP_VERSION=0.1.0
ARG SOURCE_REVISION=local
LABEL org.opencontainers.image.title="ouroboros-admin-gateway-api" \
      org.opencontainers.image.version="${APP_VERSION}-${SOURCE_REVISION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}"
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Ouroboros.AdminGateway.Api.dll"]
