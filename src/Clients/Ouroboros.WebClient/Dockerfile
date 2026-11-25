FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG APP_VERSION=0.1.0
ARG SOURCE_REVISION=local
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ouroboros.Observability/ src/Ouroboros.Observability/
COPY src/Clients/Ouroboros.WebClient/ src/Clients/Ouroboros.WebClient/
RUN dotnet publish src/Clients/Ouroboros.WebClient/Ouroboros.WebClient.csproj -c Release -o /out /p:UseAppHost=false /p:Version=${APP_VERSION} /p:SourceRevisionId=${SOURCE_REVISION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24
ARG APP_VERSION=0.1.0
ARG SOURCE_REVISION=local
LABEL org.opencontainers.image.title="ouroboros-web-client" \
      org.opencontainers.image.version="${APP_VERSION}-${SOURCE_REVISION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}"
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Ouroboros.WebClient.dll"]
