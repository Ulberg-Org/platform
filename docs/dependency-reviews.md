# Dependency reviews

A dependency needs either 1M+ weekly downloads or an official source (the project's own org). Anything else gets
its source read at the pinned version before we use it: what it runs, where it connects, what it sends out. A
version bump means a new review. (Working rule, 2026-11-20.)

## crystaldba/postgres-mcp 0.3.0 (Claude's Postgres MCP, todo's `.mcp.json`)
Small publisher, ~566k pulls in total. Reviewed at tag v0.3.0 (commit 7179ab0), about 13k lines of Python.
- **Connects to:** only the database it's given (psycopg). Its one other network client is OpenAI, used only
  when a tool is called with `method: "llm"` *and* `OPENAI_API_KEY` is set. We never set the key, so no query
  text leaves the machine. (`_langfuse_trace` is just a field in its replies, not a call.)
- **Runs:** no subprocesses, no `eval`, writes no files.
- **Read-only:** we don't rely on its "restricted" mode. The database enforces it: `todo_readonly` may only
  SELECT, its transactions are read-only by default, statements stop after 30 s.
- **Decision:** use, pinned at 0.3.0.

## kubernetes-mcp-server 0.1.3 (infra developers' Claude, this repo's `.mcp.json`)
The `containers` org, ~63k weekly downloads. Reviewed at tag v0.1.3, about 42k lines of Go.
- **Connects to:** the Kubernetes API from its kubeconfig. Its other HTTP clients (Kiali, Tempo, Loki, network
  observability, token exchange) belong to toolsets that are off unless configured. We enable only `core`.
- **Telemetry:** OTLP metrics only if `OTEL_EXPORTER_OTLP_ENDPOINT` is set (it isn't).
- **Runs:** no subprocesses.
- **Read-only:** with `read_only = true` it offers only tools marked read-only (pkg/mcp/mcp.go). The hard limit
  is its identity: the local `claude` service account with Kubernetes' `view` role (no Secrets).
- **Decision:** use, pinned at 0.1.3, toolset `core` only (not `config`, which could show the kubeconfig).
