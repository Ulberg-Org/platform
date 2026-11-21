# platform

Infrastructure and platform for the `todo` showcase (Azure, Entra, AKS, ArgoCD, observability), written as Pulumi in F#.

- Applications describe what they need in a `platform.yaml` in their own repo. This repo decides how to provide it.
- Changes are applied only by CI, after approval from a platform lead.
- People, groups and repo leads are defined as code in `access/`.

See `AGENTS.md` for the working rules and `Ulberg-Org/todo/PLAN.md` for the overall plan.

## Layout

How it fits together: an app describes itself in `platform.yaml`. The **renderer** turns that, plus an
**environment**, into Kubernetes manifests, and **Argo CD** deploys them together with the platform's
**components**: locally, a kind cluster that the **CLI** starts; in Azure, AKS, which the Pulumi **stacks** create
(applied only by CI). Claude works in a **sandbox** of its own.

| Path | What |
|---|---|
| `access/people.yaml` | People, groups, repo leads and Claude agents. The only place access is defined |
| `renderer/`, `lib/` | `platform.yaml` + an environment → Kubernetes manifests; Argo CD's plugin (D60, D68). `lib/` holds the shared parsing (parse, don't validate) |
| `components/` | What the platform runs in every cluster (Argo CD, the gateway, the telemetry stack, network baseline), one folder each: the chart's pin, values, local values |
| `environments/`, `apps/` | Each environment's settings, and the apps Argo CD runs there (D67) |
| `cli/` | The platform CLI (`platform up / sync / test / down`), its image (D63), and the files it fills in (`cli/templates/`) |
| `stacks/`, `bootstrap/` | Pulumi stacks for Azure, Entra and GitHub (`stacks/access/`: groups, PIM, Conditional Access, rulesets, environments), and the one-time setup Pulumi needs first |
| `sandbox/` | Claude's sandbox (D81): a Lima VM per profile, confined to an allowlisting proxy; `sandbox/test` checks it holds |
| `scripts/` | Claude's GitHub identity (`claude-github-token.sh`, `claude-push.sh`) and the agent's smoke test |
| `.github/` | CI (`ci.yml`), Pulumi preview and apply (`pulumi.yml`), CLI releases (`release.yml`), and the checks they run |
| `docs/` | The F# style, and the reviews of dependencies we use without an official source |
| `tests/` | Unit tests (Expecto) |

## Tools

`flake.nix` pins the tools for working on the platform (D79): `nix develop` gives developers the same versions as
Claude's sandbox (platform profile). On macOS it uses your system's .NET 11 (`brew install dotnet`).

## The platform CLI (local development)

`platform` runs an app locally the way it runs in Azure (D61, D67): a kind cluster where Argo CD deploys the
platform and the app, with Postgres outside and Grafana for telemetry. It ships as a public, signed image
(D63), so a developer needs only Docker and cosign:

```bash
brew install cosign            # verifies the image's signature (with nix-darwin: add "cosign" to the
                               # Homebrew brews in your flake, then darwin-rebuild switch)
# Docker Desktop with 12 GiB of memory, sharing your home folder (the default)
```

App repos run it through `make` at a pinned version: `make up`, `make sync`, `make test-platform`, `make down`
(see the app's README). To have `platform` on your PATH for any app repo, add this to your shell profile, with
the version and digest from the latest release (its summary lists them):

```bash
platform() {
  local image=ghcr.io/ulberg-org/platform-cli:vX.Y.Z@sha256:...
  cosign verify "$image" --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    --certificate-identity-regexp '^https://github\.com/Ulberg-Org/platform/\.github/workflows/release\.yml@refs/tags/v' >/dev/null &&
    docker run --rm -i -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD:$PWD" -w "$PWD" "$image" "$@"
}
# then, in an app repo: platform up .
```

Developing the platform itself: run the CLI from this checkout instead, which builds the renderer from it:
`PLATFORM=../platform/cli/platform make up` in the app repo.

Releasing: push a `vX.Y.Z` tag (MAJOR.MINOR.PATCH). `.github/workflows/release.yml` builds both images for arm64
and amd64, publishes them on GHCR and signs them; then bump the app repos' pins.

## Checks (all in CI, no git hooks)

```bash
dotnet tool restore
dotnet fantomas --check stacks tests
dotnet build platform.slnx -c Release
dotnet run --project tests/Access.Tests -c Release --no-build
```

`.github/workflows/pulumi.yml` runs `pulumi preview` on every PR and on `main`, and `pulumi up` on `main` only after a platform lead approves the `dev` environment.

Changing who has access: edit `access/people.yaml`, update `.github/CODEOWNERS` in every affected repo to what the preview error prints (merge the other repos' CODEOWNERS first), and open a PR.
