FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build

WORKDIR /src

# Copy files that affect dependency resolution first.
# Changes to application source won't invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props ./

COPY RecruIT.List/RecruIT.List.csproj RecruIT.List/
COPY RecruIT.FhirConstants/RecruIT.FhirConstants.csproj RecruIT.FhirConstants/

# Persist NuGet packages between builds.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    --mount=type=cache,id=nuget-http,target=/root/.local/share/NuGet/http-cache \
    dotnet restore RecruIT.List/RecruIT.List.csproj

# Now copy the actual source.
# Changes here don't invalidate the restore layer above.
COPY . .

# No --no-restore here: RecruIT.List is a Blazor project, and the SDK only adds the implicit
# Microsoft.AspNetCore.App.Internal.Assets package - which is what puts blazor.web.js under
# wwwroot/_framework - once it can see the actual .razor files. The restore layer above ran
# against just the .csproj (no source yet), so its project.assets.json is missing that package;
# reusing it via --no-restore silently produces a publish output with no wwwroot/_framework at
# all (a 404 on /_framework/blazor.web.js at runtime, not a build failure). Restoring again here,
# now that the full source is present, is cheap since the packages are already cached above.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    --mount=type=cache,id=nuget-http,target=/root/.local/share/NuGet/http-cache \
    dotnet publish RecruIT.List/RecruIT.List.csproj \
        --configuration Release \
        --output /app/publish \
        --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled-extra@sha256:cea9d5494f358fe43082300c24d1cd9f651066b6b7de1e5735bca8d3ade68649
WORKDIR /app

COPY --from=build /app/publish .

USER 1654:1654
ENTRYPOINT ["dotnet", "RecruIT.List.dll"]
