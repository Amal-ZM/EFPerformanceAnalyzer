# Multi-stage build: SDK image to compile/publish, slim ASP.NET runtime image to actually run.
# Build context is the repo root (not src/EFPerformanceAnalyzer.Api) because Api references Core
# via a project reference — both projects need to be in the build context.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY EFPerformanceAnalyzer.sln .
COPY src/EFPerformanceAnalyzer.Core/EFPerformanceAnalyzer.Core.csproj src/EFPerformanceAnalyzer.Core/
COPY src/EFPerformanceAnalyzer.Api/EFPerformanceAnalyzer.Api.csproj src/EFPerformanceAnalyzer.Api/
RUN dotnet restore src/EFPerformanceAnalyzer.Api/EFPerformanceAnalyzer.Api.csproj

COPY src/ src/
RUN dotnet publish src/EFPerformanceAnalyzer.Api/EFPerformanceAnalyzer.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
# $PORT is provided by the host (Render, etc.) at runtime; Program.cs reads it and binds to it.
# This EXPOSE is metadata only — it doesn't restrict the actual bind port.
EXPOSE 8080

ENTRYPOINT ["dotnet", "EFPerformanceAnalyzer.Api.dll"]
