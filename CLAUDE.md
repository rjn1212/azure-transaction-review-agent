# Handoff: Azure Transaction Review Agent

This file is for the next agent working in this repo. Claude Code loads `CLAUDE.md` automatically.
Read this file first, then `README.md`.

## Goal and constraints

- **Purpose:** a learning project to get hands-on with Azure hosting, Azure DevOps (Boards and
  Pipelines) and an agentic LLM app. Stack: .NET, Angular and SQL.
- **Owner:** Randy, a senior engineer with a financial-services and fraud-technology background,
  new to Azure specifically. Explain Azure and DevOps steps as you go; he wants to learn them,
  not just have them done.
- **Cost rules:**
  - Azure: free tiers only.
  - OpenAI: keep spend minimal. Use the cheapest model, and never loop real LLM calls in tests.
- **Repo and machine:**
  - GitHub: https://github.com/rjn1212/azure-transaction-review-agent (the owner pushes from
    his Mac).
  - Local clone: `~/projects/git/azure-transaction-review-agent`.
  - Machine: macOS on Apple Silicon (arm64).
- **Plan doc:** the living plan, covering architecture, setup steps, security and phases, is at
  https://claude.ai/code/artifact/bf3bb033-80c4-4a7a-874f-68a447da3a2c. Keep it roughly in sync
  if the design changes.

## What the app does

An analyst opens a flagged card-transaction alert (synthetic data) and clicks **Run agent**. The
.NET API sends the alert to OpenAI together with five read-only tools backed by SQL. The model
decides which tools to call, and `FunctionInvokingChatClient` runs them. The model then returns
**APPROVE / HOLD / ESCALATE**, validated against a JSON schema, with reasons and the policy rule
codes it applied (R1–R6). The run, its tokens, its cost and a full step-by-step trace are saved
and shown in the Angular UI. The analyst can accept or override the decision.

## Architecture

| Layer | Tech | Azure target (free) |
| --- | --- | --- |
| UI | Angular 22, standalone components, signals, runtime `public/config.json` | Static Web Apps – Free |
| API | .NET 10 minimal API, EF Core 10 | App Service – F1 Linux |
| Agent | Microsoft.Extensions.AI 10.10 + Microsoft.Extensions.AI.OpenAI | OpenAI API (owner's key) |
| DB | SQL Server (Docker locally) / Azure SQL | Azure SQL Database free offer, AutoPause |
| CI/CD | Azure Pipelines YAML (`pipelines/`), repo stays on GitHub | Azure DevOps free tier (1 hosted job, 1,800 min/mo) |
| Monitoring | App Service Application Insights auto-instrumentation (no SDK in code) | App Insights with a daily cap |

The Free Static Web Apps plan can't link an external backend, so the SPA calls the App Service
URL directly and the API enables CORS (`Cors:AllowedOrigins`).

## Repo map

```
src/TxnAgent.Data     Entities.cs, TxnDbContext.cs, DbInitializer.cs (schema + deterministic seed)
src/TxnAgent.Agent    ReviewTools.cs (5 tools), TransactionReviewAgent.cs (loop, trace, cost, guardrails),
                      Prompts.cs, AgentDecision.cs, AgentOptions.cs, AgentExceptions.cs,
                      AgentServiceCollectionExtensions.cs (IChatClient pipeline + NotConfiguredChatClient)
src/TxnAgent.Api      Program.cs (all endpoints), appsettings*.json, TxnAgent.Api.http
tests/TxnAgent.Agent.Tests   ScriptedChatClient (fake LLM) + AgentTests (6 tests)
web/                  Angular app: src/app/api.ts, config.ts, alerts/alert-list.ts, alerts/alert-detail.ts
infra/main.bicep      All Azure resources (untested)
pipelines/ci.yml, cd.yml   Azure DevOps (untested)
docker-compose.yml    Local SQL Server 2022 (amd64 image; needs Docker Desktop's Rosetta option on)
```

API endpoints, all under `/api`:

- `GET /health`
- `GET /alerts?status=`
- `GET /alerts/{id}`
- `POST /alerts/{id}/review`: calls OpenAI. Rate-limited with the `agent` policy and capped per day.
- `POST /runs/{runId}/final`, body `{decision, analyst}`
- `GET /stats`
- `POST /demo/reset`: deletes agent runs and reopens alerts. Makes no OpenAI calls.

## Key design decisions (keep these unless there's a reason)

- **Tools are read-only.** Only the API writes decisions. Invalid or unparseable model output
  becomes `ESCALATE`.
- **Tool time windows are anchored to the flagged transaction's `OccurredAt`, not `UtcNow`.** This
  keeps the seeded demo data valid indefinitely. Don't switch back to `UtcNow`.
- **Seed data is deterministic** (`Random(42)`), with 9 alerts, each labelled with an
  `ExpectedDecision` derived from rules R1–R6. `/stats` reports accuracy against these labels.
  The seeded amount is `AvgMonthlySpend / 30 * mult`, where `mult` is roughly "times the typical
  transaction". If you change a policy rule, re-check the labels.
- **Cost guardrails:**
  - `RateLimit:AgentPerMinute` = 10.
  - `OpenAI:MaxRunsPerDay` = 200.
  - `OpenAI:MaxIterations` = 8.
  - `OpenAI:MaxOutputTokens` = 1500.
  - Tools return small payloads (at most 10 recent transactions).
  - The system prompt stays static and first in the conversation, so prompt caching applies.
- **Cost per run** is computed from `OpenAI:InputPricePerMillion` / `OutputPricePerMillion`
  (0.10 / 0.50 for `gpt-6-luna`). Update these if you change the model.
- **Schema creation:** on startup, `DbInitializer` runs `Migrate()` if migrations exist, otherwise
  `EnsureCreated()`. Create the initial migration before the first run against any database;
  otherwise the database can't be migrated later.
- **Package versions are inline in each `.csproj`.** Central package management
  (`Directory.Packages.props`) was ignored on the owner's machine (restore error NU1015), so it's
  switched off. `Directory.Packages.props` is now inert and can be deleted with
  `git rm Directory.Packages.props`.
- **Secrets:**
  - Never commit secrets.
  - Local: `dotnet user-secrets` (UserSecretsId `txnagent-api-7c1e4b2a`).
  - Azure: App Service settings (`OpenAI__ApiKey`, plus connection string `Sql` of type SQLAzure).
  - The local `appsettings.Development.json` holds only the Docker SQL `sa` dev password.

## Status (as of 2026-09-25)

Done:

- [x] Plan written (see the plan doc).
- [x] Full scaffold: data, API, agent, tests, Angular UI, Bicep, pipelines, README.
- [x] Review pass: cost guardrails, time-anchoring fix, corrected seed labels, accuracy stats,
      error mapping (503/429/502), CI trigger, Bicep `clientIp`.
- [x] `dotnet test TxnAgent.slnx` builds successfully on the owner's Mac (.NET SDK installed via
      Homebrew or the Microsoft installer). Confirm all 6 tests pass.
- [x] Angular: `ng build` and `ng test` pass (Node 24.15+ required).

Not yet done or verified:

- [ ] Commit and push the current state to GitHub (may already be done; check `git status`).
- [ ] Initial EF migration.
- [ ] Local end-to-end run: Docker SQL, API and UI.
- [ ] First real OpenAI call. **The model ID `gpt-6-luna` is taken from OpenAI's pricing page but
      hasn't been tried against the owner's account yet.** If it returns a 404 or 400, pick an
      available cheap model and update the prices in `appsettings.json`.
- [ ] Azure resources: by hand first (for learning), then compare with `infra/main.bicep`, which
      has never been deployed and may need API-version or property fixes.
- [ ] Azure DevOps: org, billing link (unlocks the free hosted job), service connections
      (GitHub + ARM `sc-txnagent-dev`), variable group `txnagent-dev` (`apiAppName`,
      `apiBaseUrl`, `swaDeploymentToken`), environment `dev`, then `ci.yml` and `cd.yml`. Neither
      pipeline has been run yet.
- [ ] Phase 4 hardening: EF migration bundle step in CD, Key Vault, managed identity for SQL,
      Entra ID auth on the API, SWA auth.

## Next tasks, in order

```bash
cd ~/projects/git/azure-transaction-review-agent

# 0. Sanity
git status
dotnet --version        # expect 10.0.x
node -v                 # expect >= 24.15
dotnet test TxnAgent.slnx

# 1. Tooling for migrations
dotnet tool install -g dotnet-ef   # plus: export PATH="$PATH:$HOME/.dotnet/tools"

# 2. Initial migration (commit it)
dotnet ef migrations add InitialCreate -p src/TxnAgent.Data -s src/TxnAgent.Api -o Migrations

# 3. Local SQL
docker compose up -d

# 4. OpenAI key + run API (http://localhost:5080)
dotnet user-secrets --project src/TxnAgent.Api set "OpenAI:ApiKey" "<ask the owner to enter it>"
dotnet run --project src/TxnAgent.Api
curl -s localhost:5080/api/alerts | head   # expect 9 alerts

# 5. UI (http://localhost:4200)
cd web && npm ci && npm start
```

Then:

1. Run the agent on all 9 alerts and check the accuracy against `ExpectedDecision` on the queue
   page.
2. Tune `Prompts.cs` only if accuracy is poor. Use **Reset demo** between runs.
3. Commit and push.
4. Walk the owner through the Azure and Azure DevOps setup, explaining each step. The steps are
   in `README.md` and the plan doc.

## Working rules for the agent

- Ask the owner before any action that costs money or creates Azure resources.
- Never print or commit the OpenAI key; let the owner type it.
- Tests must never call the real OpenAI API. Use `ScriptedChatClient`.
- Keep changes small, run `dotnet test` and `ng build` after edits, and commit with clear messages.
- If you change architecture, endpoints or setup steps, update `README.md` and this file.
