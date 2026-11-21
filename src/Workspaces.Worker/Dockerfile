FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Workspaces.Worker/ src/Workspaces.Worker/
COPY src/Workspaces.Common/ src/Workspaces.Common/
COPY templates/development-workspaces/ templates/development-workspaces/
RUN dotnet publish src/Workspaces.Worker -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE /usr/share/workspaces/LICENSE
COPY LICENSES /usr/share/workspaces/LICENSES
USER $APP_UID
ENTRYPOINT ["dotnet", "Workspaces.Worker.dll"]
