"""Challenge sync: GitLab challenges repo -> in-cluster image builds -> CTFd.

Each run:
  1. `git ls-remote` the challenges repo; exit early if the commit equals the last fully
     synced one (stored on the repo PVC). FORCE_SYNC=true skips this check.
  2. Fetch the repo into the PVC (incremental, shallow).
  3. ctfcli install/sync the static challenges whose directory changed (git tree hash).
  4. For every containerized challenge, compute a content tag from the git tree hash of its
     build context; build + push only tags that are missing from the local registry, via the
     shared buildkitd.
  5. Register / update each containerized challenge as `dynamic_iac` pointing at its exact tag.

The last synced commit is only recorded when everything succeeded, so failures retry on the
next run.
"""
import base64
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path

import requests
import yaml

from .common import CtfdApi, env, log, wait_for_ctfd

REPO_DIR = Path(env("REPO_DIR", "/data/repo"))
STATE_FILE = Path(env("STATE_FILE", "/data/last-synced-commit"))
CHALLENGES_FILE = Path(env("DYNAMIC_CHALLENGES_FILE", "/config/dynamic-challenges.yaml"))

IMAGE_PREFIX = env("IMAGE_PREFIX", "registry.ctf-build.svc.cluster.local/challenges")
REGISTRY_API = env("REGISTRY_API", "http://registry.ctf-build.svc.cluster.local").rstrip("/")
BUILDKIT_ADDR = env("BUILDKIT_HOST", "tcp://buildkitd.ctf-build.svc.cluster.local:1234")
BUILD_CONCURRENCY = int(env("BUILD_CONCURRENCY", "4"))
STATIC_CONCURRENCY = int(env("STATIC_CONCURRENCY", "4"))
# Per-directory git tree hashes of static challenges that were synced successfully.
STATIC_STATE = Path(env("STATIC_STATE_FILE", "/data/static-challenges.json"))
TEAM_GLOB = env("TEAM_GLOB", "Team *")


@dataclass
class Challenge:
    path: str
    context: str = "."
    dockerfile: str = "Dockerfile"
    port: int = 80
    protocol: str = "http"
    command: list[str] | None = None
    # filled in at runtime
    meta: dict = field(default_factory=dict)
    name: str = ""
    slug: str = ""
    image: str = ""
    flag: str = ""

    @property
    def dir(self) -> Path:
        return REPO_DIR / self.path

    @property
    def context_dir(self) -> Path:
        return self.dir / self.context


# --------------------------------------------------------------------------- git

def git_auth_args() -> list[str]:
    token = env("GITLAB_TOKEN", "")
    if not token:
        return []
    user = env("GITLAB_USERNAME", "oauth2")
    basic = base64.b64encode(f"{user}:{token}".encode()).decode()
    # Header instead of credentials in the URL, so the token never lands in .git/config on the PVC.
    return ["-c", f"http.extraHeader=Authorization: Basic {basic}"]


def git(*args: str, cwd: Path | None = None, capture: bool = False) -> str:
    r = subprocess.run(["git", *git_auth_args(), *args], cwd=cwd, check=True, text=True,
                       capture_output=capture)
    return r.stdout.strip() if capture else ""


def remote_head(repo_url: str, branch: str) -> str:
    ref = f"refs/heads/{branch}" if branch else "HEAD"
    out = git("ls-remote", repo_url, ref, capture=True)
    if not out:
        sys.exit(f"[!] {ref} not found on {repo_url}")
    return out.split()[0]


def update_checkout(repo_url: str, branch: str) -> str:
    if (REPO_DIR / ".git").is_dir():
        git("remote", "set-url", "origin", repo_url, cwd=REPO_DIR)
        git("fetch", "--depth=1", "origin", branch or "HEAD", cwd=REPO_DIR)
        git("reset", "--hard", "FETCH_HEAD", cwd=REPO_DIR)
        git("clean", "-ffdx", cwd=REPO_DIR)
    else:
        if REPO_DIR.exists():
            shutil.rmtree(REPO_DIR)
        git("clone", "--depth=1", *(["--branch", branch] if branch else []), repo_url, str(REPO_DIR))
    return git("rev-parse", "HEAD", cwd=REPO_DIR, capture=True)


# --------------------------------------------------------------------------- challenges

def load_challenges() -> list[Challenge]:
    raw = yaml.safe_load(CHALLENGES_FILE.read_text()) or []
    out = []
    for entry in raw:
        c = Challenge(**entry)
        yml = c.dir / "challenge.yml"
        if not yml.exists():
            log(f"[!] Missing {yml}, skipping")
            continue
        c.meta = yaml.safe_load(yml.read_text())
        # A `deploy:` block in challenge.yml overrides the central list, so challenge authors
        # can own their own settings.
        for k, v in (c.meta.get("deploy") or {}).items():
            if hasattr(c, k):
                setattr(c, k, v)
        c.name = c.meta["name"]
        c.slug = re.sub(r"[^a-z0-9]+", "-", c.name.lower()).strip("-")
        flags = c.meta.get("flags") or []
        if flags:
            c.flag = flags[0] if isinstance(flags[0], str) else flags[0].get("content", "")
        c.image = f"{IMAGE_PREFIX}/{c.slug}:{content_tag(c)}"
        out.append(c)
    return out


def content_tag(c: Challenge) -> str:
    """Tag derived from the build inputs: identical inputs -> identical tag -> no rebuild."""
    # Path() drops "." segments, so context "." resolves to the challenge dir itself.
    tree = git("rev-parse", f"HEAD:{(Path(c.path) / c.context).as_posix()}", cwd=REPO_DIR, capture=True)
    h = hashlib.sha256(f"{tree}\0{c.dockerfile}\0{c.flag}".encode()).hexdigest()
    return h[:16]


# --------------------------------------------------------------------------- registry / build

def image_exists(image: str) -> bool:
    repo, tag = image.split("/", 1)[1].rsplit(":", 1)
    r = requests.head(
        f"{REGISTRY_API}/v2/{repo}/manifests/{tag}",
        headers={"Accept": "application/vnd.oci.image.index.v1+json, "
                           "application/vnd.oci.image.manifest.v1+json, "
                           "application/vnd.docker.distribution.manifest.v2+json"},
        timeout=10,
    )
    return r.status_code == 200


def build(c: Challenge) -> tuple[Challenge, bool, str]:
    ctx = c.context_dir
    tmp = None
    try:
        # Some Dockerfiles COPY a flag.txt that isn't committed; inject it from challenge.yml.
        df = ctx / c.dockerfile
        if c.flag and not (ctx / "flag.txt").exists() and "flag.txt" in df.read_text(errors="ignore"):
            tmp = Path(tempfile.mkdtemp(prefix=f"ctx-{c.slug}-"))
            shutil.copytree(ctx, tmp, dirs_exist_ok=True, symlinks=True)
            (tmp / "flag.txt").write_text(c.flag + "\n")
            ctx = tmp
        r = subprocess.run(
            ["buildctl", "--addr", BUILDKIT_ADDR, "build",
             "--frontend", "dockerfile.v0",
             "--local", f"context={ctx}",
             "--local", f"dockerfile={ctx}",
             "--opt", f"filename={c.dockerfile}",
             "--output", f"type=image,name={c.image},push=true",
             "--progress", "plain"],
            capture_output=True, text=True,
        )
        return c, r.returncode == 0, r.stdout + r.stderr
    finally:
        if tmp:
            shutil.rmtree(tmp, ignore_errors=True)


def build_missing(challenges: list[Challenge]) -> list[Challenge]:
    """Builds missing images in parallel. Returns challenges whose image is available."""
    todo = [c for c in challenges if not image_exists(c.image)]
    ready = [c for c in challenges if c not in todo]
    for c in ready:
        log(f"[=] {c.name}: {c.image} up to date")
    if not todo:
        return ready

    log(f"[*] Building {len(todo)} image(s), {BUILD_CONCURRENCY} at a time...")
    with ThreadPoolExecutor(BUILD_CONCURRENCY) as pool:
        for c, ok, output in pool.map(build, todo):
            if ok:
                log(f"[+] Built {c.image}")
                ready.append(c)
            else:
                tail = "\n".join(output.splitlines()[-40:])
                log(f"[!] Build FAILED for {c.name}:\n{tail}\n---")
    return ready


# --------------------------------------------------------------------------- CTFd

def register(api: CtfdApi, c: Challenge, existing: dict | None) -> None:
    meta = c.meta
    additional = {"image": c.image, "port": str(c.port), "protocol": c.protocol,
                  "host": env("CHALLENGE_HOST")}
    if c.command:
        additional["command"] = json.dumps(c.command)
    value = int(meta.get("value", 100))
    payload = {
        "name": c.name,
        "category": meta.get("category", "Misc"),
        "description": meta.get("description", ""),
        "attribution": meta.get("attribution", meta.get("author", "")),
        "value": value, "initial": value, "minimum": value, "decay": 100, "function": "linear",
        "state": meta.get("state", "visible"),
        "type": "dynamic_iac",
        "scenario": "localhost:5000/scenarios/k8s-monopod:latest",
        "timeout": 900,
        "destroy_on_flag": True,
        "shared": False,
        "mana_cost": 0,
        "additional": json.dumps(additional),
    }

    if existing and existing.get("type") != "dynamic_iac":
        log(f"[*] Converting '{c.name}' (id={existing['id']}) from {existing.get('type')} to dynamic_iac")
        api.delete(f"/challenges/{existing['id']}")
        existing = None

    if existing:
        try:
            api.patch(f"/challenges/{existing['id']}", json=payload)
            log(f"[+] Updated '{c.name}' (id={existing['id']}) -> {c.image}")
            return
        except RuntimeError as e:
            log(f"[*] Update failed ({e}); re-creating '{c.name}'")
            api.delete(f"/challenges/{existing['id']}")

    cid = api.post("/challenges", json=payload)["id"]
    log(f"[+] Created '{c.name}' (id={cid}) -> {c.image}")

    for fl in meta.get("flags") or []:
        fl = {"content": fl} if isinstance(fl, str) else fl
        api.post("/flags", json={"challenge_id": cid, "type": fl.get("type", "static"),
                                 "content": fl.get("content", ""), "data": fl.get("data", "")})
    for tg in meta.get("tags") or []:
        api.post("/tags", json={"challenge_id": cid,
                                "value": tg.get("value") if isinstance(tg, dict) else str(tg)})
    for ht in meta.get("hints") or meta.get("hint") or []:
        ht = {"content": ht} if isinstance(ht, str) else ht
        body = {"challenge_id": cid, "content": ht.get("content", ""), "cost": int(ht.get("cost", 0))}
        if ht.get("title"):
            body["title"] = ht["title"]
        api.post("/hints", json=body)
    for fpath in meta.get("files") or []:
        f = c.dir / fpath
        if f.exists():
            with f.open("rb") as fp:
                api._req("POST", "/files", files={"file": (f.name, fp)},
                         data={"challenge_id": cid, "type": "challenge"})


def find_static_dirs(dynamic: list[Challenge]) -> list[str]:
    """Challenge dirs (relative to the repo) under the team folders, minus the containerized ones.

    Same discovery as the repo's ctfd-cli-autorun_all_teams.py, but the containerized challenges
    are left out: ctfcli would sync them back to `standard`, after which register() would
    delete + recreate them as dynamic_iac and wipe their solves.
    """
    skip = {c.path for c in dynamic}
    dirs = []
    for team in sorted(REPO_DIR.glob(TEAM_GLOB)):
        for yml in team.rglob("challenge.yml"):
            if {"venv", ".venv", "__pycache__", ".git", "node_modules"} & set(yml.parts):
                continue
            rel = yml.parent.relative_to(REPO_DIR).as_posix()
            if rel not in skip:
                dirs.append(rel)
    return sorted(dirs)


def import_static(api: CtfdApi, dynamic: list[Challenge], force: bool) -> list[str]:
    """ctfcli install/sync for static challenges whose directory changed. Returns failed dirs."""
    ctf_dir = REPO_DIR / ".ctf"
    ctf_dir.mkdir(exist_ok=True)
    (ctf_dir / "config").write_text(
        f"[config]\nurl = {api.url}\naccess_token = {api.token}\n\n[challenges]\n"
    )

    state = {} if force or not STATIC_STATE.exists() else json.loads(STATIC_STATE.read_text())
    trees = {d: git("rev-parse", f"HEAD:{d}", cwd=REPO_DIR, capture=True)
             for d in find_static_dirs(dynamic)}
    changed = [d for d, t in trees.items() if state.get(d) != t]
    log(f"[*] Static challenges: {len(trees)} total, {len(changed)} changed")
    if not changed:
        return []

    existing = {c["name"] for c in api.get("/challenges?view=admin")}

    def apply(d: str) -> tuple[str, bool, str]:
        name = (yaml.safe_load((REPO_DIR / d / "challenge.yml").read_text()) or {}).get("name")
        action = "sync" if name in existing else "install"
        r = subprocess.run(["ctf", "challenge", action, d], cwd=REPO_DIR, capture_output=True, text=True)
        return d, r.returncode == 0, f"{action}: {(r.stdout + r.stderr).strip()[-1500:]}"

    failed = []
    with ThreadPoolExecutor(STATIC_CONCURRENCY) as pool:
        for d, ok, out in pool.map(apply, changed):
            if ok:
                state[d] = trees[d]
                log(f"[+] {d} ({out.split(':', 1)[0]})")
            else:
                failed.append(d)
                log(f"[!] {d} failed -> {out}")

    # Only successful dirs are recorded, so failures are retried next run.
    STATIC_STATE.write_text(json.dumps({d: t for d, t in state.items() if d in trees}, indent=1))
    return failed


# --------------------------------------------------------------------------- main

def main() -> None:
    ctfd_url = env("CTFD_URL").rstrip("/")
    repo_url = env("CHALLENGES_REPO_URL")
    branch = env("CHALLENGES_BRANCH", "")  # empty = the repo's default branch
    force = env("FORCE_SYNC", "false").lower() in ("1", "true", "yes")

    token = env("CTFD_TOKEN", "")
    if not token:
        log("[!] CTFD_TOKEN not set (Secret ctfd-admin-token missing?); bootstrap hasn't run yet.")
        sys.exit(1)
    if not env("GITLAB_TOKEN", ""):
        log("[!] GITLAB_TOKEN not set; nothing to sync.")
        return

    head = remote_head(repo_url, branch)
    last = STATE_FILE.read_text().strip() if STATE_FILE.exists() else ""
    if head == last and not force:
        log(f"[=] {branch or 'HEAD'}@{head[:10]} already synced, nothing to do.")
        return

    wait_for_ctfd(ctfd_url)
    api = CtfdApi(ctfd_url, token)
    if not api.token_valid():
        sys.exit("[!] CTFd rejected the admin token; re-run the bootstrap (ArgoCD sync).")

    commit = update_checkout(repo_url, branch)
    log(f"[*] Syncing {branch or 'HEAD'}@{commit[:10]} (previous: {last[:10] or 'none'})")

    challenges = load_challenges()
    failed = import_static(api, challenges, force)

    ready = build_missing(challenges)

    existing = {c["name"]: c for c in api.get("/challenges?view=admin")}
    failed += [c.name for c in challenges if c not in ready]
    for c in ready:
        try:
            register(api, c, existing.get(c.name))
        except RuntimeError as e:
            log(f"[!] Registering '{c.name}' failed: {e}")
            failed.append(c.name)

    if failed:
        sys.exit(f"[!] {len(failed)} challenge(s) failed: {', '.join(failed)}. Will retry next run.")
    STATE_FILE.write_text(commit + "\n")
    log(f"[+] Sync of {commit[:10]} complete.")


if __name__ == "__main__":
    main()
