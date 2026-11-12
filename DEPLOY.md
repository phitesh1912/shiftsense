# Deploying ShiftSense

One container serves the chat UI, the REST API and the MCP HTTP endpoint. SQLite is created and
seeded on first start, so the container is effectively stateless and safe to redeploy.

## 1. Environment variables

| Variable | Default | Notes |
|---|---|---|
| `GROQ_API_KEY` | — | Free key from <https://console.groq.com>; selects Groq's OpenAI-compatible endpoint and `openai/gpt-oss-120b`. Empty (and no `LLM_API_KEY`) → deterministic fake model. |
| `LLM_API_KEY` | — | Any other OpenAI-compatible provider; takes precedence over `GROQ_API_KEY` |
| `LLM_ENDPOINT` | Groq, or Gemini's `.../v1beta/openai/` with `LLM_API_KEY` | Endpoint for `LLM_API_KEY` |
| `LLM_MODEL` | `openai/gpt-oss-120b` / `gemini-2.5-flash` | Model name at that endpoint |
| `EVAL_PACE_SECONDS` | `10` | Eval runner only: delay between scenarios with a real model (free tiers cap tokens/minute) |
| `SHIFTSENSE_DB` | `shiftsense.db` | SQLite path (`/data/shiftsense.db` in the image) |
| `SHIFTSENSE_FIXED_NOW` | `true` | Freeze "now" at 2026-11-18 09:00 UTC so the seeded fortnight is always current. `false` = real clock |
| `SHIFTSENSE_USER` | `E002` | **stdio MCP host only**: the employee the server acts as |
| `OTEL_CONSOLE` | — | `true` prints OpenTelemetry spans (agent runs, model calls, tool calls) to the console |
| `PORT` | `8080` | Set by Render; the entrypoint binds `http://+:$PORT` |

Get a free Groq key at <https://console.groq.com> → *API Keys*. Never commit it.

## 2. Render (free tier)

`render.yaml` is a Blueprint.

1. Push the repo to GitHub.
2. Render → **New → Blueprint** → pick the repo. It proposes one Docker web service `shiftsense`
   (free plan, Singapore; change `region` if you like).
3. Paste `GROQ_API_KEY` when prompted, or leave it empty to run the fake model.
4. **Apply**. First build ≈ 3 minutes (SDK image restore + two publishes).
5. Open the URL: the chat UI is at `/`, `/health` shows provider/model/tools, MCP is at `/mcp`.

Free-tier notes: the instance sleeps after 15 idle minutes (first request ≈ 30 s), and the disk is
ephemeral — the DB is reseeded on each boot, which is what you want for a demo.

## 3. Docker anywhere

```bash
docker build -t shiftsense .
docker run --rm -p 8080:8080 -e GROQ_API_KEY=... shiftsense
# fake model:
docker run --rm -p 8080:8080 shiftsense
```

Multi-stage build (`sdk:8.0` → `aspnet:8.0`), non-root user, `HEALTHCHECK` on `/health`. The image
also contains the stdio MCP host at `/app/mcp/ShiftSense.Mcp.dll` for use with
`docker exec`/`docker run -i`.

## 4. Azure Container Apps (optional)

```bash
az group create -n shiftsense-rg -l southeastasia
az acr create -n shiftsenseacr -g shiftsense-rg --sku Basic --admin-enabled true
az acr build -r shiftsenseacr -t shiftsense:latest .
az containerapp env create -n shiftsense-env -g shiftsense-rg -l southeastasia
az containerapp create -n shiftsense -g shiftsense-rg --environment shiftsense-env \
  --image shiftsenseacr.azurecr.io/shiftsense:latest --registry-server shiftsenseacr.azurecr.io \
  --target-port 8080 --ingress external --min-replicas 0 --max-replicas 1 \
  --secrets llm-key=<KEY> --env-vars LLM_API_KEY=secretref:llm-key
```

## 5. Connecting MCP clients

### Claude Desktop (stdio)

Publish once so the client doesn't need the SDK's build step each launch:

```bash
dotnet publish src/ShiftSense.Mcp -c Release -o out/mcp
```

`claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "shiftsense": {
      "command": "dotnet",
      "args": ["C:/path/to/shiftsense/out/mcp/ShiftSense.Mcp.dll"],
      "env": {
        "SHIFTSENSE_USER": "E002",
        "SHIFTSENSE_DB": "C:/path/to/shiftsense/shiftsense.db"
      }
    }
  }
}
```

Restart Claude Desktop; the seven tools appear under the tools icon. Switch `SHIFTSENSE_USER` to
`E001` (a manager) to unlock `get_team_coverage` and reports' data. Logs go to stderr so they never
corrupt the protocol stream.

### VS Code (stdio or HTTP)

`.vscode/mcp.json`:

```json
{
  "servers": {
    "shiftsense": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["${workspaceFolder}/out/mcp/ShiftSense.Mcp.dll"],
      "env": { "SHIFTSENSE_USER": "E001" }
    },
    "shiftsense-remote": {
      "type": "http",
      "url": "https://<your-render-app>.onrender.com/mcp",
      "headers": { "X-Employee-Id": "E001" }
    }
  }
}
```

### Any client over HTTP

Streamable HTTP, stateless. `POST /mcp` with `Accept: application/json, text/event-stream`,
`Content-Type: application/json` and `X-Employee-Id: E002`. Requests without the header get `401`
before the MCP handler runs. In production the header is replaced by the bearer token the rest of
the API uses; nothing below the host changes.

## 6. Running the eval against a real model

```bash
GROQ_API_KEY=... dotnet run --project eval/ShiftSense.Eval
```

Writes `eval/results/results_llm.md` with tool-selection accuracy and task success per category.
That file is the only sanctioned source for numbers in the README.

## 7. Local development

```bash
dotnet test
dotnet run --project src/ShiftSense.Api          # UI + API + /mcp
dotnet run --project src/ShiftSense.Mcp          # stdio host (talks MCP on stdin/stdout)
OTEL_CONSOLE=true dotnet run --project src/ShiftSense.Api   # with spans printed
```
