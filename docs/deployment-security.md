# Production deployment security

## Local development

The checked-in source imports Tailwind CSS and web fonts through npm. On a clean checkout, run `npm ci` and `npm run build:assets` before `dotnet run`; the build emits the ignored CSS, font, and SignalR files into `wwwroot/`. Run `dotnet test NewsFeed.Tests/NewsFeed.Tests.csproj` for the SQL-free integration suite. `test_backend.ps1` is a PowerShell wrapper for those same in-memory tests and does not touch a developer or production SQL database.

## HTTPS and reverse proxy

Run the ASP.NET service on `127.0.0.1:4050`; the workflow sets this address so port 4050 is not exposed directly. Install Nginx and copy `deploy/nginx/sinhala-news.conf.template` to `/etc/nginx/sites-available/sinhala-news.conf`. Replace `news.example.com` with the domain whose DNS A record points to the VPS. Obtain a certificate with Certbot, then enable and reload the Nginx site. Keep ports 80 and 443 open and close public port 4050 after confirming Nginx can reach the loopback listener. The app honors one forwarded proxy hop and enables HSTS and HTTPS redirection outside Development and Testing.

A real domain is required for a publicly trusted TLS certificate; the IP address alone does not provide the domain certificate configured in the template. Do not enable this template until DNS points to the server and Certbot has created the referenced certificate files.

## Required server settings

Set values in `/etc/sinhala-news/sinhala-news.env` or the service manager, never in checked-in settings:

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:4050
ConnectionStrings__DefaultConnection=Server=sqlserver;Database=CdNewsfeedDb;User Id=sa;Password=<secret>;TrustServerCertificate=True;MultipleActiveResultSets=true;
AdminAuth__Username=<administrator-name>
AdminAuth__Password=<unique-strong-password-at-least-16-characters>
AdminAuth__SessionVersion=1
PublicBaseUrl=https://news.example.com
```

Use a unique administrator password with upper and lower case letters, a number, and a symbol. To revoke every existing administrator cookie during a password or account incident, rotate the password and increment `AdminAuth__SessionVersion`. User sessions are separate. `Cors__AllowedOrigins__0` may be set to an additional trusted HTTPS origin only when a separate browser client needs cross-origin access; same-origin deployment needs no CORS entry.

## Database migrations and backups

Fresh databases are created by EF Core migrations at startup. The first startup on a legacy database runs idempotent schema additions, creates missing feature tables, and records the current schema as the baseline. Review the migration and SQL changes before applying them to production. The main-branch deployment workflow runs `database/backup-sqlserver.sh` before restarting the service; it creates a copy-only backup with checksum and runs `RESTORE VERIFYONLY`. Backups are stored with mode `0600` under `/var/backups/sinhala-news`.

The helper expects the existing SQL Server container name `creolive-sql2025`, database `CdNewsfeedDb`, and the SA password in the container environment. Override `SQL_CONTAINER`, `SQL_DATABASE`, or `SQL_BACKUP_DIR` when the deployment differs. Verify the backup directory is on persistent storage and copy backups to an independent protected location regularly.

For recovery, restore a selected backup to a separate staging database first, run `RESTORE VERIFYONLY`, start the application against that staging database, and check users, posts, comments, images, and admin access. Schedule a maintenance window before replacing production data. Preserve the old database and backup until the restored production app is verified. Never test a restore against the live database.

## CI and secrets

GitHub Actions runs xUnit integration tests against EF Core InMemory, builds the local Tailwind/SignalR/font assets, and publishes the application. Configure `VPS_HOST`, `VPS_USERNAME`, and `VPS_SSH_KEY` as GitHub Actions secrets. The deploy job backs up the database first and restarts the service only after tests pass. CI does not configure DNS, Nginx, firewall rules, or certificates; apply those operations manually after confirming the chosen domain.
