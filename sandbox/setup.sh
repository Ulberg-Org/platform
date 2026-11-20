#!/usr/bin/env bash
# One-time setup of Docker Sandboxes on a developer's machine (D76, D78), run by the developer, not by Claude.
# Every sandbox is deny-by-default: it reaches only the hosts below. The developer's SSH keys never enter.
# Undo with `sbx policy reset` and `sbx settings unset ssh.agentForwardingEnabled`.
set -euo pipefail

# No SSH agent in the sandbox: Claude pushes with its own GitHub app token only (D52, D78).
sbx settings set ssh.agentForwardingEnabled false
sbx daemon restart >/dev/null

# Locked Down: nothing is reachable without an allow rule.
sbx policy ls >/dev/null 2>&1 || sbx policy init deny-all

allow=(
  # GitHub: code, pull requests, releases, container images.
  github.com api.github.com "**.githubusercontent.com" ghcr.io
  # Image registries the platform pulls from.
  registry-1.docker.io auth.docker.io production.cloudflare.docker.com
  mcr.microsoft.com "**.data.mcr.microsoft.com" quay.io "**.quay.io" registry.k8s.io "**.pkg.dev"
  # Packages: NuGet, npm (MCP servers), Nix (the repos' flakes, D79).
  api.nuget.org "**.nuget.org" registry.npmjs.org cache.nixos.org channels.nixos.org
  # Helm chart repositories (components/*/component.yaml, cli/versions.env).
  helm.cilium.io argoproj.github.io grafana-community.github.io prometheus-community.github.io open-telemetry.github.io
  # Grafana's plugins and their storage.
  grafana.com storage.googleapis.com
  # Claude itself (and its web search, which runs on Anthropic's side).
  api.anthropic.com
)
sbx policy allow network "$(IFS=,; echo "${allow[*]}")"
sbx policy ls
