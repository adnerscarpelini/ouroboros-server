FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ouroboros.Messaging/ src/Ouroboros.Messaging/
COPY src/Ouroboros.BuildingBlocks/ src/Ouroboros.BuildingBlocks/
COPY src/Ouroboros.Observability/ src/Ouroboros.Observability/
COPY src/Services/Notifications/ src/Services/Notifications/
RUN dotnet publish src/Services/Notifications/Ouroboros.Notifications.Worker/Ouroboros.Notifications.Worker.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /app/.local/notifications-emails && chown -R $APP_UID /app/.local
USER $APP_UID
ENTRYPOINT ["dotnet", "Ouroboros.Notifications.Worker.dll"]
