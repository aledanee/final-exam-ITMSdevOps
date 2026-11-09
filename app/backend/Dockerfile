# build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY TaskManager.csproj ./
RUN dotnet restore TaskManager.csproj

COPY Program.cs ./
RUN dotnet publish TaskManager.csproj -c Release -o /app/publish /p:UseAppHost=false

# runtime stage (slim image, serves via built-in Kestrel web server)
# note: .NET 8's Debian-slim variant is tagged 8.0-bookworm-slim (there is no 8.0-slim)
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# run as the image's built-in non-root user (never as root)
USER $APP_UID

ENTRYPOINT ["dotnet", "TaskManager.dll"]
