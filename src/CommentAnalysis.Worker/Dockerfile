# Build context: repository root.
#   docker build -f src/CommentAnalysis.Worker/Dockerfile -t comment-analysis-worker .
#
# Base images are pinned to a patch version and its digest, so a build is reproducible and a moved tag cannot change
# what ships. The flip side: a pinned digest also freezes the OS layers, and MCR re-pushes the same tag with Debian
# security fixes without a new .NET patch. So refresh the digests at least monthly and on every .NET patch release,
# not only when the .NET version changes. Take the digest of the multi-arch index from the registry with
# `docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:<tag>` (the top-level Digest), and keep the
# runtime patch equal to the Microsoft.* package versions in Directory.Packages.props (bump both in one change).

FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=7461 \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

# Non-root user provided by the official .NET images.
USER $APP_UID
EXPOSE 7461

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "CommentAnalysis.Worker.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "CommentAnalysis.Worker.dll"]
