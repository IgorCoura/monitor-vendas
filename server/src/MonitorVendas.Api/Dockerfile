FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MonitorVendas.Api/MonitorVendas.Api.csproj src/MonitorVendas.Api/
RUN dotnet restore src/MonitorVendas.Api/MonitorVendas.Api.csproj
COPY src/ src/
RUN dotnet publish src/MonitorVendas.Api/MonitorVendas.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MonitorVendas.Api.dll"]
