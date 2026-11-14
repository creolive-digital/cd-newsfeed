$ErrorActionPreference = "Stop"

# Build ignored static assets first. The integration suite uses a uniquely named
# EF Core in-memory database and never connects to or mutates SQL Server.
Push-Location $PSScriptRoot
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit code $LASTEXITCODE)." }
    npm run build:assets
    if ($LASTEXITCODE -ne 0) { throw "The web asset build failed (exit code $LASTEXITCODE)." }

    $env:USE_INMEMORY_DB = "true"
    dotnet test (Join-Path $PSScriptRoot 'NewsFeed.Tests/NewsFeed.Tests.csproj') --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "The NewsFeed integration test suite failed (exit code $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}
