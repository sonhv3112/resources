#!/usr/bin/env python3
"""GpmLite <-> Camoufox fingerprint sidecar.

Thin bridge so the .NET app can use Camoufox's own (BrowserForge-backed) fingerprint
brain without reimplementing it. Stateless: reads a JSON request on argv/stdin, prints a
JSON reply on stdout. All randomness lives here; the .NET app stores and replays the
`config` it returns so each profile keeps a stable identity across launches.

Commands (first arg):
  generate   Create a fresh, coherent Camoufox fingerprint config for an OS.
             in : {"os":"windows|macos|linux", "screen":{...}?, "fonts":[...]?,
                   "webgl":["vendor","renderer"]?, "ff_version":int?}
             out: {"ok":true, "config":{...}}        # store this per profile

  spec       Build a concrete launch spec, pinning a stored config and applying the
             runtime proxy/geo. The .NET app sets `env`, writes `user_prefs` to the
             profile's user.js, and launches `executable_path` with `args`.
             in : {"os":..., "config":{...stored...}, "proxy":{...}?, "geoip":true|"1.2.3.4"?,
                   "headless":bool?, "block_webrtc":bool?, "executable_path":str?}
             out: {"ok":true, "executable_path":str, "args":[...],
                   "env":{"CAMOU_CONFIG_1":...}, "user_prefs":{...}, "proxy":{...}|null}

  version    {"ok":true, "camoufox": "<installed version>"}

On error: {"ok":false, "error":"..."} and exit code 1.

Identity vs. location: `generate` strips locale/timezone/geolocation/language keys from the
stored config so they follow the proxy (via geoip) at launch; the hardware/browser identity
(navigator, screen, webgl, canvas, fonts) stays pinned.
"""
import json
import os
import sys
from pathlib import Path


def _point_to_bundled_camoufox() -> None:
    """Make Camoufox use the Camoufox bundled next to this sidecar instead of the per-user cache
    (~/Library/Caches/camoufox). On a fresh machine that cache does not exist, so camoufox.pkgman
    would fail with "Version information not found ... run `camoufox fetch`". The bundled install dir
    holds version.json + Camoufox.app (mac) / camoufox.exe (win). We repoint pkgman.INSTALL_DIR to it;
    installed_verstr()/launch_path() read that global at call time, so this takes effect everywhere.
    """
    candidates = []
    env_dir = os.environ.get("GPMLITE_CAMOUFOX_DIR")
    if env_dir:
        candidates.append(Path(env_dir))
    # Relative to the frozen executable: <app>/Resources/camoufox (mac) or <app>/camoufox (win).
    base = Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve().parent
    for up in (base, base.parent, base.parent.parent, base.parent.parent.parent):
        candidates.append(up / "camoufox")
    for c in candidates:
        try:
            if c and (c / "version.json").exists():
                import camoufox.pkgman as pk
                pk.INSTALL_DIR = c
                os.environ["GPMLITE_CAMOUFOX_DIR"] = str(c)
                return
        except Exception:
            pass  # fall back to Camoufox's default (per-user cache) — dev machines have it

# Keys that should follow the proxy (geoip) rather than be pinned to the stored identity.
VOLATILE_PREFIXES = (
    "timezone",
    "locale:",
    "geolocation:",
    "navigator.language",
    "navigator.languages",
)


def _reassemble_config(env: dict) -> dict:
    """Concat CAMOU_CONFIG_1..N env chunks back into the config dict."""
    parts = []
    i = 1
    while f"CAMOU_CONFIG_{i}" in env:
        parts.append(str(env[f"CAMOU_CONFIG_{i}"]))
        i += 1
    if not parts:
        return {}
    return json.loads("".join(parts))


def _strip_volatile(config: dict) -> dict:
    return {
        k: v
        for k, v in config.items()
        if not any(k == p or k.startswith(p) for p in VOLATILE_PREFIXES)
    }


def cmd_generate(req: dict) -> dict:
    from camoufox.utils import launch_options

    kw = {"os": req.get("os", "windows"), "headless": True}
    if req.get("screen"):
        from browserforge.fingerprints import Screen  # type: ignore
        s = req["screen"]
        kw["screen"] = Screen(
            min_width=s.get("min_width"), max_width=s.get("max_width"),
            min_height=s.get("min_height"), max_height=s.get("max_height"),
        )
    if req.get("fonts"):
        kw["fonts"] = req["fonts"]
    if req.get("webgl"):
        kw["webgl_config"] = tuple(req["webgl"])
    if req.get("ff_version"):
        kw["ff_version"] = int(req["ff_version"])

    opts = launch_options(**kw)
    config = _reassemble_config(opts.get("env", {}))
    config = _strip_volatile(config)
    return {"ok": True, "config": config}


def cmd_spec(req: dict) -> dict:
    from camoufox.utils import launch_options

    # Screen/window dimensions are machine-specific: a config generated on a 1728px-wide display
    # crashes Camoufox on a 1512px monitor ("needs 1728 pixels, but this monitor has 1512").
    # Drop those keys so Camoufox fits the window to the CURRENT monitor (it always constrains to it).
    # The stable identity (navigator/webgl/canvas/fonts/audio) is kept.
    config = dict(req.get("config") or {})
    for k in list(config):
        if k.startswith(("screen.", "window.", "document.body.")):
            del config[k]

    kw = {
        "os": req.get("os", "windows"),
        "config": config,
        "headless": bool(req.get("headless", False)),
    }

    # Force the OUTER WINDOW to fit the SMALLEST monitor attached. Camoufox's native layer refuses to
    # launch when window.outerWidth exceeds the monitor ("needs N pixels, but this monitor has M"), and
    # a fingerprint generated on a big screen (e.g. 1920/1728) otherwise crashes on a 1512 laptop. By
    # pinning the window to the current machine's smallest display (screeninfo reports LOGICAL px on
    # macOS, which is exactly what the native check compares), the profile opens on ANY screen size.
    # The window param drives window.outerWidth/outerHeight in generate_fingerprint(); the reported
    # screen.* stays realistic for the launching machine. Margins leave room for the menu bar/taskbar.
    try:
        from screeninfo import get_monitors
        mons = [m for m in get_monitors() if m.width and m.height]
        if mons:
            min_w = min(m.width for m in mons)
            min_h = min(m.height for m in mons)
            kw["window"] = (max(800, min_w - 40), max(600, min_h - 120))
    except Exception:
        pass  # fall back to Camoufox's default window handling

    # We replay a config that Camoufox itself generated (coherent), so suppress the
    # "manually setting properties" guard — this is an intentional, pinned identity.
    kw["i_know_what_im_doing"] = True
    if req.get("executable_path"):
        kw["executable_path"] = req["executable_path"]
    if req.get("proxy"):
        kw["proxy"] = req["proxy"]
    if "geoip" in req and req["geoip"] is not None:
        kw["geoip"] = req["geoip"]
    if req.get("block_webrtc") is not None:
        kw["block_webrtc"] = bool(req["block_webrtc"])
    if req.get("locale"):
        kw["locale"] = req["locale"]

    # Resilience: a geoip lookup can fail (no network for geoip=true, an unreachable maxmind entry,
    # a weird proxy IP). Never let that stop the profile from opening — retry without geoip so the
    # browser still launches (timezone just won't be pinned to the IP). One rollout, no hard failures.
    try:
        opts = launch_options(**kw)
    except Exception:
        if "geoip" in kw:
            kw.pop("geoip", None)
            opts = launch_options(**kw)
        else:
            raise
    # launch_options merges the whole current environ; return ONLY the Camoufox-specific
    # vars so the .NET app can inherit its own environment and nothing leaks into the browser.
    full_env = opts.get("env", {}) or {}
    env = {
        k: v
        for k, v in full_env.items()
        if k.startswith("CAMOU_CONFIG") or k == "FONTCONFIG_PATH"
    }
    return {
        "ok": True,
        "executable_path": opts.get("executable_path"),
        "args": opts.get("args", []),
        "env": env,
        "user_prefs": opts.get("firefox_user_prefs", {}),
        "proxy": opts.get("proxy"),
    }


def cmd_version(_req: dict) -> dict:
    from camoufox.pkgman import installed_verstr
    return {"ok": True, "camoufox": installed_verstr()}


def main() -> int:
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "missing command"}))
        return 1
    _point_to_bundled_camoufox()
    cmd = sys.argv[1]
    raw = sys.argv[2] if len(sys.argv) > 2 else sys.stdin.read()
    raw = raw.lstrip("﻿")  # tolerate a UTF-8 BOM from the caller's stdin writer
    try:
        req = json.loads(raw) if raw.strip() else {}
    except json.JSONDecodeError as e:
        print(json.dumps({"ok": False, "error": f"bad JSON request: {e}"}))
        return 1

    table = {"generate": cmd_generate, "spec": cmd_spec, "version": cmd_version}
    fn = table.get(cmd)
    if fn is None:
        print(json.dumps({"ok": False, "error": f"unknown command: {cmd}"}))
        return 1
    try:
        print(json.dumps(fn(req), ensure_ascii=False))
        return 0
    except Exception as e:  # noqa: BLE001 - report any failure as JSON
        import traceback
        print(json.dumps({"ok": False, "error": str(e), "trace": traceback.format_exc()}))
        return 1


if __name__ == "__main__":
    # When frozen with PyInstaller, multiprocessing (pulled in by playwright/camoufox) re-executes
    # this binary to spawn workers. Without freeze_support() the child re-runs main() and prints a
    # second, bogus JSON line — which the caller may read instead of the real reply. This must be the
    # first thing that runs in the child.
    import multiprocessing
    multiprocessing.freeze_support()
    sys.exit(main())
