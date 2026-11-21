# Claude's sandbox image (D76–D79): Docker's own Claude sandbox image (with its Docker engine) plus exactly the tools in the repo's flake
# (`packages.tools`), built by Nix and copied in with only what they depend on (least footprint). Built by
# sandbox/sandbox from the repo it's for, which is the build context:
#   docker build -f <platform repo>/sandbox/Dockerfile <app or platform repo>

FROM nixos/nix:2.34.0@sha256:b9c9611c8530fa8049a1215b20638536e1e71dcaf85212e47845112caf3adeea AS tools
WORKDIR /src
COPY flake.nix flake.lock ./
RUN nix --extra-experimental-features "nix-command flakes" build .#tools -o /tools \
 && mkdir /closure && cp -a $(nix-store --query --requisites /tools) /closure/

FROM docker.io/sbx/claude:latest@sha256:6769221b4de7e194f8ae8b0111ca64a1dec7182a53bd0792471cacca3b661d31
USER root
COPY --from=tools /closure /nix/store
COPY --from=tools /tools /opt/tools
# Claude's identity (D52, D78): commits as the app, and GitHub over HTTPS, which the sandbox's proxy signs with the
# app's token (there's no SSH key in here).
RUN git config --system url."https://github.com/".insteadOf "git@github.com:" \
 && git config --system user.name "ulberg-claude[bot]" \
 && git config --system user.email "358816865+ulberg-claude[bot]@users.noreply.github.com"
ENV PATH=/opt/tools/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
USER agent
