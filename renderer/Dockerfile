# The renderer as Argo CD's `platform` plugin (D68): a sidecar of Argo CD's repo server runs it for every app
# and for the root application. Chiseled: no shell, no package manager, nothing to write to but /tmp.
# Built from the platform repo's root: docker build -f renderer/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS build
WORKDIR /src
# Restore first, from the project files only, so the package layer is reused when only code changes.
COPY Directory.Build.props global.json ./
COPY lib/Platform.Parsing/Platform.Parsing.fsproj lib/Platform.Parsing/
COPY renderer/Platform.Renderer/Platform.Renderer.fsproj renderer/Platform.Renderer/
RUN dotnet restore renderer/Platform.Renderer/Platform.Renderer.fsproj
COPY lib/ lib/
COPY renderer/ renderer/
RUN dotnet publish renderer/Platform.Renderer/Platform.Renderer.fsproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:11.0.0-resolute-chiseled
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/Platform.Renderer.dll"]
