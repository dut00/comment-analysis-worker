# Build context: repository root.
#   docker build -f src/CommentAnalysis.Worker/Dockerfile -t comment-analysis-worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in a separate layer so it is cached until project files or package versions change.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/CommentAnalysis.Core/CommentAnalysis.Core.csproj src/CommentAnalysis.Core/
COPY src/CommentAnalysis.Infrastructure/CommentAnalysis.Infrastructure.csproj src/CommentAnalysis.Infrastructure/
COPY src/CommentAnalysis.Worker/CommentAnalysis.Worker.csproj src/CommentAnalysis.Worker/
RUN dotnet restore src/CommentAnalysis.Worker/CommentAnalysis.Worker.csproj

COPY src/ src/
RUN dotnet publish src/CommentAnalysis.Worker/CommentAnalysis.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

# Non-root user provided by the official .NET images.
USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "CommentAnalysis.Worker.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "CommentAnalysis.Worker.dll"]
