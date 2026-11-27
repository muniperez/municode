FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Municode.Worker/ src/Municode.Worker/
COPY src/Municode.Common/ src/Municode.Common/
COPY templates/development-workspaces/ templates/development-workspaces/
RUN dotnet publish src/Municode.Worker -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE /usr/share/workspaces/LICENSE
COPY LICENSES /usr/share/workspaces/LICENSES
USER $APP_UID
ENTRYPOINT ["dotnet", "Municode.Worker.dll"]
