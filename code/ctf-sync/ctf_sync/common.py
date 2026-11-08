import os
import sys
import time

import requests


def env(name: str, default: str | None = None) -> str:
    value = os.environ.get(name, default)
    if value is None:
        sys.exit(f"[!] Missing required environment variable {name}")
    return value


def log(msg: str) -> None:
    print(msg, flush=True)


def wait_for_ctfd(ctfd_url: str, timeout: int = 600) -> None:
    log(f"[*] Waiting for CTFd at {ctfd_url}...")
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if requests.get(f"{ctfd_url}/healthcheck", timeout=5).ok:
                return
        except requests.RequestException:
            pass
        time.sleep(3)
    sys.exit("[!] CTFd did not become healthy in time")


class CtfdApi:
    def __init__(self, url: str, token: str):
        self.url = url.rstrip("/")
        self.token = token
        self.s = requests.Session()
        self.s.headers["Authorization"] = f"Token {token}"

    def _req(self, method: str, path: str, **kw) -> requests.Response:
        if "files" not in kw:
            kw.setdefault("headers", {})["Content-Type"] = "application/json"
        r = self.s.request(method, f"{self.url}/api/v1{path}", timeout=30, **kw)
        if not r.ok:
            raise RuntimeError(f"{method} {path} -> {r.status_code}: {r.text[:300]}")
        return r

    def get(self, path, **kw):
        return self._req("GET", path, **kw).json().get("data")

    def post(self, path, **kw):
        return self._req("POST", path, **kw).json().get("data")

    def patch(self, path, **kw):
        return self._req("PATCH", path, **kw).json().get("data")

    def delete(self, path, **kw):
        self._req("DELETE", path, **kw)

    def token_valid(self) -> bool:
        try:
            r = self.s.get(f"{self.url}/api/v1/users/me", timeout=10)
            return r.ok
        except requests.RequestException:
            return False
