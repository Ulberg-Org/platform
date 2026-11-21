# The platform CLI as an image (D63): `platform up | sync | test | down` with every tool it needs, so a developer
# only needs Docker (and cosign to verify the image). It drives the host's Docker through its socket.
# Released by .github/workflows/release.yml (a vX.Y.Z tag), signed with cosign. Built from the repo's root:
#   docker build -f cli/Dockerfile --build-arg RENDERER_IMAGE=<renderer image@digest> .
# Every download is checked against the SHA-256 pinned here (D65); the base image is pinned by digest.

FROM debian:trixie-slim@sha256:a72b4433de1cf7d6bae0c4cee74d0547f58601a643b3375f070c51600d63b6b2
ARG TARGETARCH

RUN apt-get update \
 && apt-get install -y --no-install-recommends bash ca-certificates coreutils curl git jq tar gzip \
 && rm -rf /var/lib/apt/lists/*

# name, URL per architecture, SHA-256 per architecture.
ARG KIND_VERSION=v0.33.0
ARG KIND_SHA256_arm64=20022bee6cfcd5086cb7234d218e3454e6090022f2a8f55d1fa7fcf42c3867a2
ARG KIND_SHA256_amd64=aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d
ARG KUBECTL_VERSION=v1.37.2
ARG KUBECTL_SHA256_arm64=fcc16f63186659d9722014b7e02d5fdd2d4e669cb8c0ab66365d7d3b8ba03509
ARG KUBECTL_SHA256_amd64=c04487523e7aa7d1493f7808174c899db93d7a9cf57531420cd76b9bd415f8f4
ARG HELM_VERSION=v4.3.2
ARG HELM_SHA256_arm64=df31cb61680ef728f82c770e5280dd565617bee6b039d37e85c677288be4bbb0
ARG HELM_SHA256_amd64=d1c0278d46beab79582d7e50f5b3dc206944ba8e577cde618513fd07daa53983
ARG COSIGN_VERSION=v3.1.4
ARG COSIGN_SHA256_arm64=ade83b7aac62e131cacfe96d958948b2150cc9efec46a570a2649b8e27dedad2
ARG COSIGN_SHA256_amd64=424e2c97d0b45fd7a6b608a87f090f35633f4c23ece5f092d48bba11aaf4bbfa
ARG BUILDX_VERSION=v0.38.2
ARG BUILDX_SHA256_arm64=46c0f334772de3cb0b341cc415caf7339fb5cfc8b8438ebcb5a10e7f23800d36
ARG BUILDX_SHA256_amd64=7992ec09413b80ff57ba4e081f56fc833d7bac1d5e71143c6c809e7e6065f6d6
# Docker publishes no checksums for its static binaries; these are the SHA-256 of the packages as downloaded.
ARG DOCKER_VERSION=29.9.3
ARG DOCKER_SHA256_arm64=1b27fd739378ef938401e81ce5afb73b7ad7432d4f598814a9ff5f826bb464d2
ARG DOCKER_SHA256_amd64=3fb5d1f13014c18cc54945bc177c58a9056a482dada031e17d12cf2e6922c973

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
RUN fetch() { curl -sSfL "$1" -o "$2" && echo "$3  $2" | sha256sum --check --quiet; }; \
    arch=$TARGETARCH; docker_arch=$([ "$arch" = arm64 ] && echo aarch64 || echo x86_64); \
    sum() { local v="${1}_SHA256_${arch}"; echo "${!v}"; }; \
    fetch "https://github.com/kubernetes-sigs/kind/releases/download/${KIND_VERSION}/kind-linux-${arch}" /usr/local/bin/kind "$(sum KIND)"; \
    fetch "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${arch}/kubectl" /usr/local/bin/kubectl "$(sum KUBECTL)"; \
    fetch "https://github.com/sigstore/cosign/releases/download/${COSIGN_VERSION}/cosign-linux-${arch}" /usr/local/bin/cosign "$(sum COSIGN)"; \
    fetch "https://get.helm.sh/helm-${HELM_VERSION}-linux-${arch}.tar.gz" /tmp/helm.tgz "$(sum HELM)"; \
    tar -xzf /tmp/helm.tgz -C /usr/local/bin --strip-components 1 "linux-${arch}/helm"; \
    fetch "https://download.docker.com/linux/static/stable/${docker_arch}/docker-${DOCKER_VERSION}.tgz" /tmp/docker.tgz "$(sum DOCKER)"; \
    tar -xzf /tmp/docker.tgz -C /usr/local/bin --strip-components 1 docker/docker; \
    mkdir -p /usr/local/lib/docker/cli-plugins; \
    fetch "https://github.com/docker/buildx/releases/download/${BUILDX_VERSION}/buildx-${BUILDX_VERSION}.linux-${arch}" /usr/local/lib/docker/cli-plugins/docker-buildx "$(sum BUILDX)"; \
    chmod +x /usr/local/bin/* /usr/local/lib/docker/cli-plugins/docker-buildx; rm -f /tmp/*.tgz

# The CLI and what local development reads from the platform repo. Not environments/dev.yaml or anything else
# about Azure: the image is public (D63), the platform behind it isn't.
WORKDIR /platform
COPY cli/ cli/
COPY database/ database/
COPY components/ components/
COPY apps/ apps/
COPY environments/local.yaml environments/

# The renderer image this release runs (Argo CD's plugin and the CLI's plan), by digest.
ARG RENDERER_IMAGE
ARG VERSION=dev
RUN test -n "$RENDERER_IMAGE" && printf 'RENDERER_IMAGE=%s\nPLATFORM_VERSION=%s\n' "$RENDERER_IMAGE" "$VERSION" > cli/release.env

ENTRYPOINT ["/platform/cli/platform"]
