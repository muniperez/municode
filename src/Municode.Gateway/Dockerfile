FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Municode.Gateway/ src/Municode.Gateway/
COPY src/Municode.Common/Extensions/DevelopmentWorkspaceConfigurationExtensions.cs src/Municode.Common/Extensions/
COPY src/Municode.Common/Services/DevelopmentWorkspaceGatewayOptions.cs src/Municode.Common/Services/
RUN dotnet publish src/Municode.Gateway -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE /usr/share/workspaces/LICENSE
COPY LICENSES /usr/share/workspaces/LICENSES
USER $APP_UID
ENTRYPOINT ["dotnet", "Municode.Gateway.dll"]
