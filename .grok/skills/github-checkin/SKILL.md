---
name: github-checkin
description: >
  Check in / commit / push Dispatch.MVC to GitHub using a fine-grained PAT
  stored in a gitignored secrets file (never in this SKILL.md). Triggers on
  "check in", "checkin", "push to github", "git push", "commit these changes",
  "here's the PAT", "parallel-jobs", "worker-service".
metadata:
  short-description: "Push Dispatch.MVC with a gitignored PAT; never echo or commit the token"
user-invocable: true
---

# GitHub check-in (Dispatch.MVC)

Default repo: `sean-mcclain-eugene/Dispatch.MVC`  
Default branch: `parallel-jobs` (unless the user names another).

## Secrets (do not put the PAT in this file)

Read, never print: `.grok/secrets/github.env` (gitignored).

```
GITHUB_PAT=github_pat_...
GITHUB_OWNER=sean-mcclain-eugene
GITHUB_REPO=Dispatch.MVC
GITHUB_BRANCH=parallel-jobs
```

Copy `.grok/secrets/github.env.example` and fill in a fine-grained PAT with
**Contents: Read and write** on this repo only.

The Grok GitHub connector is **read-only**. Push with
`https://x-access-token:${TOKEN}@github.com/...` and redact `github_pat_*`
from logs. Never force-push over the user’s commits. Never add `.grok/secrets/`
to a commit.

If the user pastes a PAT in chat, write it to `.grok/secrets/github.env`
(`chmod 600`) and use that; do not put it in this SKILL.md.
