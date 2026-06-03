# 1. Use the SDK image to build and publish the app
FROM ://microsoft.com AS build
WORKDIR /src

# Copy all files and restore dependencies
COPY . .
RUN dotnet restore

# Build and publish a release optimization package
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# 2. Use the minimal runtime image to run the app
FROM ://microsoft.com AS final
WORKDIR /app
COPY --from=build /app/publish .

# Render exposes traffic via port 10000 by default, or reads the PORT env var
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "LuduBackend.dll"]
