# Setup

## Prerequisites

- .NET 8 SDK and [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- A Dynamics 365 / Dataverse environment containing the tables described in [data-model.md](data-model.md)
- An Entra ID app registration added to Dataverse as an **application user**, with a security role that has:
  - Read on the profile, data source and rule tables, and on every table you score;
  - Create, Read, Write and **Assign** on Health Score Result.
- An Azure OpenAI resource with a chat model deployment

## 1. Configure settings

```bash
cp src/HealthScore.Functions/local.settings.example.json src/HealthScore.Functions/local.settings.json
```

| Setting | Purpose |
|---|---|
| `DataverseUrl` | Environment URL, e.g. `https://<org>.crm.dynamics.com` |
| `DataverseClientId` / `DataverseClientSecret` / `DataverseTenantId` | App registration credentials |
| `OpenAIEndpoint` / `OpenAIKey` / `OpenAIDeploymentName` | Azure OpenAI |
| `HealthScoreAppId` | Optional. Makes record links open inside a specific model-driven app |

`local.settings.json` is git-ignored. In Azure, set the same values under the Function App's **Environment variables**.

## 2. Run locally

```bash
cd src/HealthScore.Functions
func start
```

```bash
curl "http://localhost:7071/api/CheckProfileReadiness?profileId=<guid>"
curl "http://localhost:7071/api/EvaluateHealthScoreProfile?profileId=<guid>&recordId=<guid>&preview=true&debug=true"
```

## 3. Deploy

Publish from Visual Studio, or run:

```bash
func azure functionapp publish <function-app-name>
```

## 4. Web resources

| File | Where |
|---|---|
| `src/webresources/reliance_rulecondition.js` | Rule form: add it as a form library, then call `prefillCondition` on load and when the Data Source lookup changes (pass the execution context) |
| `src/webresources/reliance_healthdashboard.html` | Upload as a Webpage (HTML) web resource. Add it to a dashboard (untick *Restrict cross-frame scripting*) or as a page in the app's navigation |

The dashboard's "View all" links use three system views on Health Score Result. Set their IDs in `CONFIG.views` at the top of the dashboard script. Opened outside Dynamics, the dashboard runs on sample data.
