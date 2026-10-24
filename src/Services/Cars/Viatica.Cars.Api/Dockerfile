# syntax=docker/dockerfile:1

# The Cars service. Build from the repository root:
#   docker build -f src/Services/Cars/Viatica.Cars.Api/Dockerfile -t viatica/cars .
# Run "migrate" once against an empty database, then the image with no arguments:
#   docker run --rm -e ConnectionStrings__Cars="Host=...;Database=cars;..." viatica/cars migrate
#
# Base images are pinned by digest as well as tag: a tag moves, so two builds of one commit could otherwise ship
# different runtimes. The tags are the .NET 11 RC2 ones; the SDK image carries 11.0.100-rc.2.26504.105, exactly
# the version global.json pins.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2@sha256:e2438fd800a09d2d761aefe4edd30675c8f1db8958e4423c0844cc9a0240e60f AS build
WORKDIR /source

# global.json is copied unchanged. Its "paths" list .dotnet before $host$; the folder does not exist in the image,
# so the SDK resolver moves on to the image's own SDK. The .editorconfig comes along because analyzer severities
# live in it and warnings are errors: without it the image would build code the local build rejects, or the reverse.
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/ src/Contracts/
COPY src/Kernel/ src/Kernel/
COPY src/Services/Cars/ src/Services/Cars/

# Restore and publish in one step over a shared package cache. Split in two, a publish sharing the cache mount
# with parallel image builds could miss packages its own restore had just extracted.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Services/Cars/Viatica.Cars.Api/Viatica.Cars.Api.csproj --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2@sha256:ca60823900e34e488caed1df187266fa02e3b59d1180c8db728b6cb727212ba2 AS runtime
WORKDIR /app

# The image ships a non-root user; nothing here needs root.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Viatica.Cars.Api.dll"]
