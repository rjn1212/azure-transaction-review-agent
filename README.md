# Azure Transaction Review Agent

A small, end-to-end **agentic AI** app on Azure free tiers. An analyst opens a flagged card
transaction; an LLM agent decides which data to look up (customer profile, recent history,
merchant risk, review policy) by calling tools backed by Azure SQL, then returns
**APPROVE / HOLD / ESCALATE** with reasons and cited policy rules. Every tool call is saved and
shown as a trace. All data is synthetic.

| Layer | Tech | Azure (free) |
| --- | --- | --- |
| UI | Angular 22 (standalone, signals) | Static Web Apps – Free |
| API | .NET 10 minimal API, EF Core 10 | App Service – F1 Linux |
| Agent | Microsoft.Extensions.AI + OpenAI (tool calling, structured output) | OpenAI API (your key) |
| Data | Azure SQL | Azure SQL Database free offer (auto-pause) |
| CI/CD, backlog | Azure Pipelines, Azure Boards | Azure DevOps free tier |

```
Angular (SWA) ──HTTPS/CORS──> .NET API (App Service) ──tools──> Azure SQL
                                      │
                                      └──chat + tool calls──> OpenAI
```

## Repo layout

```
src/TxnAgent.Api      Minimal API endpoints, DI, CORS, startup seeding
src/TxnAgent.Agent    Agent: prompts, tools (ReviewTools), loop + trace (TransactionReviewAgent)
src/TxnAgent.Data     EF Core entities, DbContext, synthetic seed data
tests/…Agent.Tests    xUnit tests using a scripted fake LLM (zero OpenAI cost)
web/                  Angular app (queue, alert detail, run agent, trace, analyst override)
infra/main.bicep      All Azure resources on free tiers
pipelines/            Azure DevOps ci.yml and cd.yml
docker-compose.yml    Local SQL Server for development
```

## How the agent works

1. `POST /api/alerts/{id}/review` builds a chat with a system prompt and the alert id.
2. The model is given 5 read-only tools (`get_transaction`, `get_customer_profile`,
   `get_recent_transactions`, `get_merchant_risk`, `get_review_policy`).
3. `FunctionInvokingChatClient` runs whichever tools the model asks for, feeds results back,
   and repeats (capped at `OpenAI:MaxIterations`, default 8).
4. The final answer is forced into the `AgentDecision` JSON schema. Invalid output → `ESCALATE`.
5. Decision, tokens, cost and the full trace are saved (`AgentRun`, `AgentStep`).
6. Tool time windows are anchored to the flagged transaction's timestamp, so the seeded demo data
   gives the same answers months later.

### Evaluating the agent

Every seeded alert carries an `ExpectedDecision` derived from the policy rules (R1–R6). The queue
page shows **accuracy vs expected** (latest run per alert) next to analyst agreement. Use
**Reset demo** (or `POST /api/demo/reset`) to clear runs and re-run all alerts, e.g. after changing
the prompt or the model, and compare accuracy and cost.

## Run locally (Mac)

Prereqs: .NET 10 SDK, Node 24.15+, Docker Desktop, `dotnet tool install -g dotnet-ef`.

On Apple Silicon, turn on Docker Desktop → Settings → General → "Use Rosetta for x86/amd64
emulation" (SQL Server only ships amd64 images). Alternative: skip Docker and point
`ConnectionStrings:Sql` (user-secrets) at your Azure SQL free database with your IP allowed.

```bash
# 1. Database
docker compose up -d

# 2. Restore, build, test (tests never call OpenAI)
dotnet test TxnAgent.slnx

# 3. Create the initial EF migration once and commit it - BEFORE the first run against any database.
#    (Without migrations the app falls back to EnsureCreated, which can't be migrated later.)
dotnet ef migrations add InitialCreate -p src/TxnAgent.Data -s src/TxnAgent.Api -o Migrations

# 4. OpenAI key -> user-secrets (never in appsettings / git)
dotnet user-secrets --project src/TxnAgent.Api set "OpenAI:ApiKey" "sk-..."

# 5. API on http://localhost:5080 (creates schema + seeds data on first run)
dotnet run --project src/TxnAgent.Api

# 6. UI on http://localhost:4200
cd web && npm ci && npm start
```

`src/TxnAgent.Api/TxnAgent.Api.http` has ready-made requests for VS Code / Rider / Visual Studio.

## Cost control

- Default model `gpt-6-luna` (~$0.10 / $0.50 per 1M input/output tokens) → about **$0.002 per review**.
- Tools return small payloads (max 10 recent transactions); loop capped at 8 iterations;
  `MaxOutputTokens` 1500. System prompt is static and first, so prompt caching applies.
- Each run records tokens and cost; the queue page shows total spend.
- The API has no login, so anyone with the URL could trigger runs. Two guardrails cap the damage:
  `RateLimit:AgentPerMinute` (10) and `OpenAI:MaxRunsPerDay` (200 ≈ $0.40/day on luna).
  For real use, add App Service Authentication (Entra ID) in front of the API.
- Set a monthly budget on your OpenAI project and a $5 budget alert in Azure Cost Management.

## Deploy to Azure

Option A (learn the portal): create the resources by hand following the plan doc.
Option B (repeatable):

```bash
az login
az group create -n rg-txnagent-dev -l centralus
az deployment group create -g rg-txnagent-dev -f infra/main.bicep \
  -p namePrefix=txnagent-rn sqlAdminPassword='<strong password>' openAiApiKey='<sk-...>' \
     clientIp=$(curl -s ifconfig.me)
```

Note: `namePrefix` must make names globally unique (SQL server, web app, SWA).

Outputs give the API and web URLs. Tear everything down with `az group delete -n rg-txnagent-dev`.

## Azure DevOps

1. Create an org at dev.azure.com and a private project **TxnAgent**.
2. Organization settings → Billing → link your Azure subscription (unlocks the free hosted job:
   1,800 min/month).
3. Project settings → Service connections:
   - **GitHub** (Azure Pipelines GitHub app) for this repo.
   - **Azure Resource Manager** (workload identity federation) scoped to `rg-txnagent-dev`,
     named `sc-txnagent-dev`.
4. Pipelines → Library → variable group **txnagent-dev**:
   `apiAppName` = `api-txnagent-rn`, `apiBaseUrl` = `https://api-txnagent-rn.azurewebsites.net/api`,
   `swaDeploymentToken` (secret; from the Static Web App → Manage deployment token).
5. Pipelines → Environments → create **dev** (optionally add an approval check).
6. Pipelines → New → GitHub → this repo → Existing YAML: `/pipelines/ci.yml`, then `/pipelines/cd.yml`.
7. Boards: install the Azure Boards GitHub app; reference work items in commits with `AB#123`.

## Roadmap

- [x] Phase 0–3 scaffold: data, API, agent with tools, UI with trace and override
- [ ] Provision Azure resources and connect pipelines
- [ ] EF migration bundle step in CD (instead of migrate-on-startup)
- [ ] Key Vault references for secrets; managed identity for SQL
- [ ] App Service Authentication (Entra ID) on the API; SWA auth on the UI
- [ ] Compare models (luna vs sol) on the seeded scenarios; track analyst agreement rate
