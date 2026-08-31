# Dispatch — MVC + Windows Worker Service

ASP.NET Core 8 guide for long jobs on IIS:

1. **Dispatch.Web** — Razor site. Writes a `Pending` row. Holds a browser lease. Does **not** run the job.
2. **Dispatch.Worker** — Windows Worker Service. Claims the row from the shared database and runs it **outside `w3wp`**.
3. **Dispatch.Core** — model, DbContext, processor, lock/claim helpers.

Detach mid-job emails a session link. Close the tab *without* detach and the worker stops (attached lease).

This is the IIS-safe version of the original `LongJobWebApp` demo.

## Run locally

.NET 8 SDK. Open `Dispatch.sln` and set **multiple startup projects** (Web + Worker), or two terminals:

```bash
dotnet run --project Dispatch.Web
dotnet run --project Dispatch.Worker
```

Web listens at `http://localhost:5288`. SQLite is created at `App_Data/dispatch.db` (shared by both processes).

If only the site is running, jobs stay **Queued**.

## Walk the demo

1. Start a job. Status is leased to the tab.
2. Close the tab → **Abandoned**. Worker stops.
3. Or **Run in the background** → night shift. Close the tab; Worker keeps going.
4. Inbox → **Open session** (`/Job/Session/{id}`).

## Debug as a local Windows Service

`dotnet run --project Dispatch.Worker` is fine for stepping in Visual Studio. To debug the **service** host (same SCM path as production, including cwd = System32 unless pinned):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-worker-debug.ps1
```

Self-elevates, publishes **Debug** to `artifacts\worker-debug`, registers **DispatchWorkerDebug** (Manual start, `DOTNET_ENVIRONMENT=Development`), and points it at `App_Data\dispatch.db` so it shares sqlite with `dotnet run --project Dispatch.Web`. Re-run the script after code changes. Attach VS to `Dispatch.Worker.exe`.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-worker-debug.ps1 -Uninstall
```

Production install remains `install-worker.ps1` (`DispatchWorker`, Automatic, `C:\Services\Dispatch.Worker`).

## Install on Windows / IIS

**Site.** Publish `Dispatch.Web` to IIS (in-process or out-of-process). No special app-pool tricks required for the *jobs* — they are not in the pool.

**Worker.** Elevated PowerShell from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-worker.ps1
```

That publishes to `C:\Services\Dispatch.Worker` and registers `DispatchWorker` as Automatic, with restart-on-crash. Uninstall:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-worker.ps1
```

**Same database.** Point both `appsettings.json` files at one SQL Server catalog (recommended) or one sqlite file, e.g.

```
Data Source=C:\ProgramData\Dispatch\dispatch.db;Cache=Shared
```

The unzip default (`App_Data/dispatch.db`) is rewritten at runtime to the sibling `App_Data` folder of each project. That is fine on a dev box. It is **not** fine when Web lives under `inetpub` and the service lives under `C:\Services` — they would open different files. Use an absolute path or SQL Server in production.

Swap sqlite for SQL Server in both `Program.cs` files (`UseSqlServer`) without touching the claim/processor code.

## Parallel jobs (1–5)

Five clients can Start at once. Each row is a unique `JobId`. The worker runs at most `Worker:MaxConcurrentJobs` (clamped 1–5, default 3). Extra work stays `Pending`.

All SQL is `WHERE JobId = @id`. Production SQL Server takes row locks; jobs do not contend on a table lock. The claim `ExecuteUpdate` is the mutex — two processes cannot run the same guid.

Zombies are bounded:

| Guard | Effect |
|---|---|
| `SemaphoreSlim` | No sixth in-flight task |
| `MaxJobDuration` (default 1h) | Cancel → `Failed`, do not resume |
| In-flight set | This process will not reclaim its own running ids |
| `LockUntilUtc` (45s) | Dead process → another instance resumes |
| Browser lease | Attached job stops when the tab closes |

```json
"Worker": {
  "MaxConcurrentJobs": 3,
  "MaxJobDuration": "01:00:00"
}
```

## Locks, not channels

| Before (in-process `BackgroundService`) | After |
|---|---|
| `Channel<Guid>` inside `w3wp` | `Pending` / `LockUntilUtc` rows in the database |
| App pool recycle drops work | Worker is a separate service; expired locks are reclaimed |
| Idle timeout kills detached jobs | IIS idle timeout does not touch the worker |
| Closed tab leaked work unless you leased | Lease still exists; Worker honors it |

`IJobProcessor` is unchanged. Only the **host** changed.

## Map

| Piece | Role |
|---|---|
| `Dispatch.Web/Controllers/JobController.cs` | Insert Pending, heartbeat, abandon, detach |
| `Dispatch.Worker/JobWorker.cs` | Poll + claim loop (`AddWindowsService`) |
| `Dispatch.Core/Services/JobClaimer.cs` | Optimistic lock via `ExecuteUpdate` |
| `Dispatch.Core/Services/JobProcessor.cs` | Steps, lease watch, completion email |
| `scripts/install-worker.ps1` | Publish + `New-Service` (production) |
| `scripts/install-worker-debug.ps1` | Local Debug service + shared sqlite |

## Production notes

- One worker instance with `MaxConcurrentJobs` 1–5 is enough for this demo. Several instances are safe: claim is a single `ExecuteUpdate`.
- Hangfire + SQL Server is the usual next dashboard/retry layer. Keep “web writes a row, worker runs it.”
- `IEmailSender` is the mail seam (Inbox table today; SendGrid/Graph/SMTP tomorrow).
- Attached vs detached is unchanged: only night shift outlives the browser.
