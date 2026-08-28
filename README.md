# Dispatch.Mvc

ASP.NET Core 8 MVC guide for a long-running job that:

1. Starts from a Razor form
2. Runs on a `BackgroundService` (the HTTP request is already finished)
3. Can be **detached mid-job** when it is taking too long
4. Emails the user a **link to the session** when it completes

This is the fleshed-out version of the original `LongJobWebApp` demo (broken markup, hosted-service DI, console-only email, LocalDB). Open this folder in Visual Studio or run it with the .NET 8 SDK.

```bash
dotnet run
```

Then browse to the URL the console prints (launch profile is `http://localhost:5288`).

SQLite is created at `App_Data/dispatch.db` on first start. No SQL Server.

## Walk the demo

1. Pick a job (sales rollup, extract, index, invoices, inventory).
2. Enter an email. This is where the night-shift note will be filed — not a login.
3. **Start job.** You land on a live status page that polls `/Job/Progress/{id}`.
4. **Run in the background** before it finishes. That is detach.
5. Open **Inbox**. When the worker completes it files a message with **Open session**.
6. The session is a capability URL: `/Job/Session/{id}`. Bookmark it; no auth.

If you never detach, the status page just redirects to the session when the worker finishes. No email.

## Map of the code

| File | What to copy |
|---|---|
| `Controllers/JobController.cs` | Enqueue + redirect. Never run the job on the request. |
| `Services/IBackgroundTaskQueue.cs` | `Channel<Guid>` seam. |
| `Services/QueuedHostedService.cs` | `BackgroundService` that owns the loop. |
| `Services/JobProcessor.cs` | The work. `ExecuteUpdate` so Detach cannot be overwritten. |
| `Services/IEmailSender.cs` | Swap this for SendGrid / Graph / SMTP. |
| `Services/InboxEmailSender.cs` | Demo mailbox so the link is clickable without a mail server. |
| `Views/Job/Status.cshtml` + `wwwroot/js/status.js` | Live steps. |
| `Views/Job/Detached.cshtml` | Night shift. |
| `Views/Job/Session.cshtml` | Results at the emailed URL. |
| `Views/Home/Pattern.cshtml` | Same guide, in the running app. |

## Pattern (copy this into another .NET app)

**Controller** writes a row, captures the public base URL (the worker has no `HttpContext`), enqueues the id, redirects:

```csharp
_db.Jobs.Add(job);
await _db.SaveChangesAsync();
_queue.QueueJob(job.JobId);
return RedirectToAction(nameof(Status), new { id = job.JobId });
```

**Hosted service** dequeues and creates a scope per job (so you get a scoped `DbContext`):

```csharp
await using var scope = _scopes.CreateAsyncScope();
var processor = scope.ServiceProvider.GetRequiredService<IJobProcessor>();
await processor.RunAsync(jobId, stoppingToken);
```

**Do not** inject `BackgroundService` into the controller. That was the original DI failure: hosted services are singletons registered by `AddHostedService`, not a thing you new-up per request.

**Detach** is a flag, not a cancel:

```csharp
job.IsDetached = true;
await _db.SaveChangesAsync();
```

The worker always writes progress. After the last step it **re-reads** `IsDetached` and `NotifyEmail`, then sends:

```
{PublicBaseUrl}/Job/Session/{jobId}
```

## Bugs that were in the original zip

- `Start.cshtml` — truncated form tag, submit never posted.
- `Detached.cshtml` / `Completed.cshtml` — broken home links (`/Home/IndexReturn Home`).
- `JobController` constructed with `BackgroundWorker`, which is a hosted service.
- Email: `Console.WriteLine` to `{UserName}@yourcompany.com`.
- Worker held a tracked entity across `Task.Delay` and skipped updates when detached, so Detach could be clobbered and the session had no log.
- No `_Layout`, no polling, SQL Server LocalDB connection string.

## Production upgrades

- **Queue:** Hangfire, Quartz, Azure Service Bus, RabbitMQ, or a Postgres outbox. Keep “controller enqueues an id.”
- **Mail:** implement `IEmailSender` with SendGrid, Microsoft Graph, or `SmtpClient`. Leave the Inbox table as an audit log if useful.
- **Live UI:** SignalR from the processor instead of 700 ms polling.
- **Store:** `UseSqlServer` / `UseNpgsql` in `Program.cs`. Add real EF migrations instead of `EnsureCreated`.
- **Auth:** gate `/Job/Session` by owner, and still put a tokenized link in the email.
- **Multi-instance:** the in-process `Channel` is single-host. Use an external queue before you scale out.

## Layout of a port

```
HTTP POST  →  Job row + Queue.QueueJob(id)  →  302 Status
                  │
                  ▼
         QueuedHostedService (BackgroundService)
                  │
                  ▼
              IJobProcessor
                  │
        ┌─────────┴──────────┐
        │ still attached      │ detached
        │ poll → Session      │ Inbox email → Session
        └─────────────────────┘
```
