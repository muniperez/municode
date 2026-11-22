FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Workspaces.Gateway/ src/Workspaces.Gateway/
COPY src/Workspaces.Common/Extensions/DevelopmentWorkspaceConfigurationExtensions.cs src/Workspaces.Common/Extensions/
COPY src/Workspaces.Common/Services/DevelopmentWorkspaceGatewayOptions.cs src/Workspaces.Common/Services/
RUN dotnet publish src/Workspaces.Gateway -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE /usr/share/workspaces/LICENSE
COPY LICENSES /usr/share/workspaces/LICENSES
USER $APP_UID
ENTRYPOINT ["dotnet", "Workspaces.Gateway.dll"]
