FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Municode.Api/ src/Municode.Api/
COPY src/Municode.Common/ src/Municode.Common/
COPY templates/development-workspaces/image/manifest.lock.json templates/development-workspaces/image/manifest.lock.json
RUN dotnet publish src/Municode.Api -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE /usr/share/municode/LICENSE
COPY LICENSES /usr/share/municode/LICENSES
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Municode.Api.dll"]
