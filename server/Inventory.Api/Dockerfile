FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/Inventory.Api/Inventory.Api.csproj server/Inventory.Api/
RUN dotnet restore server/Inventory.Api/Inventory.Api.csproj
COPY server/Inventory.Api/ server/Inventory.Api/
RUN dotnet publish server/Inventory.Api/Inventory.Api.csproj -c Release -o /app --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Inventory.Api.dll"]
