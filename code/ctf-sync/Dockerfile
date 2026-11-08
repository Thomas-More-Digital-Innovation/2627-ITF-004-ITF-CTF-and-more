# Image for the in-cluster CTFd bootstrap + challenge sync.
# Built in-cluster by the ctf-sync-image hook Job (code/k8s/ctf/challenges-sync.yaml) and
# pushed to registry.ctf-build.svc.cluster.local, so runs never download tooling at runtime.
FROM moby/buildkit:v0.16.0 AS buildkit

FROM python:3.11-slim

ARG KUBECTL_VERSION=v1.33.4

RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl" -o /usr/local/bin/kubectl \
 && chmod +x /usr/local/bin/kubectl

COPY --from=buildkit /usr/bin/buildctl /usr/local/bin/buildctl

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY ctf_sync/ ./ctf_sync/

ENV PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "-m"]
CMD ["ctf_sync.sync"]
