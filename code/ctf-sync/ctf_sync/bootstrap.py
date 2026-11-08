"""One-time CTFd bootstrap, run as an ArgoCD Sync hook.

1. Completes CTFd's /setup wizard if it hasn't run yet.
2. Ensures the Secret `ctfd-admin-token` holds a valid admin API token (minted inside the
   CTFd pod, since a fresh CTFd has no token yet).
3. Sets the active theme.

Idempotent: a valid existing token is reused.
"""
import base64
import re
import subprocess
import sys

import requests

from .common import CtfdApi, env, log, wait_for_ctfd

NAMESPACE = "ctf"
TOKEN_SECRET = "ctfd-admin-token"
THEME = "bsides-limburg"

MINT_SNIPPET = (
    "from CTFd import create_app\n"
    "from CTFd.models import Users\n"
    "from CTFd.utils.security.auth import generate_user_token\n"
    "app = create_app()\n"
    "with app.app_context():\n"
    "    admin = Users.query.filter_by(type='admin').first()\n"
    "    tok = generate_user_token(admin, expiration=None, description='ctf-sync')\n"
    "    print('MINTED_TOKEN=' + tok.value)\n"
)


def run_setup(ctfd_url: str) -> None:
    s = requests.Session()
    r = s.get(f"{ctfd_url}/setup", allow_redirects=False, timeout=15)
    if r.status_code != 200:
        log("[*] CTFd already initialized.")
        return
    log("[*] Performing initial CTFd setup...")
    m = re.search(r"['\"]?csrfNonce['\"]?\s*:\s*['\"]([^'\"]+)['\"]", r.text) or re.search(
        r'name="nonce"[^>]*value="([^"]+)"', r.text
    )
    if not m:
        sys.exit("[!] Could not find setup nonce")
    s.post(
        f"{ctfd_url}/setup",
        data={
            "ctf_name": "CyberKit CTF",
            "ctf_description": "CyberKit CTF Environment",
            "user_mode": "users",
            "name": env("CTFD_ADMIN_USER", "admin"),
            "email": env("CTFD_ADMIN_EMAIL", "admin@ctf.local"),
            "password": env("CTFD_ADMIN_PASSWORD", "admin"),
            "ctf_theme": THEME,
            "nonce": m.group(1),
        },
        timeout=30,
    ).raise_for_status()


def read_existing_token() -> str | None:
    r = subprocess.run(
        ["kubectl", "get", "secret", "-n", NAMESPACE, TOKEN_SECRET, "-o", "jsonpath={.data.token}"],
        capture_output=True,
        text=True,
    )
    if r.returncode != 0 or not r.stdout.strip():
        return None
    return base64.b64decode(r.stdout.strip()).decode()


def mint_token() -> str:
    out = subprocess.check_output(
        ["kubectl", "exec", "-n", NAMESPACE, "deploy/ctfd", "--", "python3", "-c", MINT_SNIPPET],
        text=True,
    )
    m = re.search(r"MINTED_TOKEN=(\S+)", out)
    if not m:
        sys.exit("[!] Token minting produced no token")
    return m.group(1)


def store_token(token: str) -> None:
    manifest = subprocess.check_output(
        ["kubectl", "create", "secret", "generic", TOKEN_SECRET, "-n", NAMESPACE,
         f"--from-literal=token={token}", "--dry-run=client", "-o", "yaml"],
        text=True,
    )
    subprocess.run(["kubectl", "apply", "-f", "-"], input=manifest, text=True, check=True)


def main() -> None:
    ctfd_url = env("CTFD_URL").rstrip("/")
    wait_for_ctfd(ctfd_url)
    run_setup(ctfd_url)

    token = read_existing_token()
    if token and CtfdApi(ctfd_url, token).token_valid():
        log("[*] Existing admin token is valid.")
    else:
        token = mint_token()
        store_token(token)
        log(f"[+] Minted new admin token into Secret {TOKEN_SECRET}.")

    CtfdApi(ctfd_url, token).patch("/configs", json={"ctf_theme": THEME})
    log(f"[*] Active theme set to {THEME}.")


if __name__ == "__main__":
    main()
