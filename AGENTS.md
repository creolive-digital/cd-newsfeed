# Repository Guidelines

## Project Structure & Module Organization

This repository is an ASP.NET Core application targeting .NET 10. `Program.cs` configures the web app and API endpoints. Keep persistence code in `Data/`, entity and DTO types in `Models/`, and integrations/background work in `Services/`. The static client and its icons, manifest, and service worker live in `wwwroot/`. Project configuration is in `cd-newsfeed.csproj`, `Properties/launchSettings.json`, and `appsettings*.json`. `docs/` contains design/reference material. Build output under `bin/` and `obj/` is generated; do not edit it.

## Build, Test, and Development Commands

- `dotnet restore` resolves NuGet dependencies.
- `dotnet build` compiles the application and reports compiler diagnostics.
- `dotnet run` starts the app using the configured launch profile (HTTP at `http://localhost:5163`).
- `dotnet run --urls http://localhost:5055` starts it at the URL expected by `test_backend.ps1`.
- `./test_backend.ps1` runs the backend integration checks; start the app and ensure its SQL Server database is available first.

## Coding Style & Naming Conventions

Follow the existing C# style: four-space indentation, nullable reference types enabled, PascalCase for types and public members, and camelCase for locals and parameters. Keep related API, data, and service logic in their existing folders; use descriptive names such as `NewsUpdaterService`. Frontend assets should remain in `wwwroot/` and use clear, lowercase filenames. No formatter or linter is configured; keep changes consistent with nearby code and avoid unrelated generated-file edits.

## Testing Guidelines

The `NewsFeed.Tests/` xUnit project uses `WebApplicationFactory` and an isolated EF Core in-memory database. Run it with `dotnet test NewsFeed.Tests/NewsFeed.Tests.csproj`; set `USE_INMEMORY_DB=true` when running the app in CI. `test_backend.ps1` remains an optional integration script for a running SQL Server instance.

## Commit & Pull Request Guidelines

Git history is not present in this checkout, so no established commit format can be verified. Use concise imperative commit subjects (for example, `Add feed pagination`). Pull requests should summarize behavior changes, mention relevant configuration or database effects, link related issues when available, and include test commands/results. Include screenshots for visible frontend changes.

## Security & Configuration

Keep credentials, API keys, and machine-specific connection strings out of committed settings. Store Gemini keys in Development with indexed secrets such as `dotnet user-secrets set "Gemini:ApiKeys:0" "<key>"`; deployment can provide matching `Gemini__ApiKeys__0` environment variables (or one `GEMINI_API_KEY`). Configure a live SQL Server using `ConnectionStrings__DefaultConnection`. Review configuration changes for accidental secret exposure before submitting.

The GitHub Actions workflow deploys `main` after tests and publish succeed. Set the repository secrets `VPS_HOST`, `VPS_USERNAME`, and `VPS_SSH_KEY`; the SSH user needs write access to `/var/www/sinhala-news` and permission to restart `sinhala-news.service`.
