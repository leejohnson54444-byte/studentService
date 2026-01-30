# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files
COPY ["StudentService.WebApp/StudentService.WebApp/StudentService.WebApp.csproj", "StudentService.WebApp/StudentService.WebApp/"]
COPY ["StudentService.WebApp/StudentService.WebApp.Client/StudentService.WebApp.Client.csproj", "StudentService.WebApp/StudentService.WebApp.Client/"]
COPY ["StudentService.Domain/StudentService.Domain.csproj", "StudentService.Domain/"]

# Restore dependencies
RUN dotnet restore "./StudentService.WebApp/StudentService.WebApp/StudentService.WebApp.csproj"

# Copy all source code
COPY . .

# Build the application
WORKDIR "/src/StudentService.WebApp/StudentService.WebApp"
RUN dotnet build "./StudentService.WebApp.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Publish stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./StudentService.WebApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "StudentService.WebApp.dll"]
