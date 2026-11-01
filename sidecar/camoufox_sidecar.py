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
import sys

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

    kw = {
        "os": req.get("os", "windows"),
        "config": req.get("config") or {},
        "headless": bool(req.get("headless", False)),
        # We replay a config that Camoufox itself generated (coherent), so suppress the
        # "manually setting properties" guard — this is an intentional, pinned identity.
        "i_know_what_im_doing": True,
    }
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

    opts = launch_options(**kw)
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
    cmd = sys.argv[1]
    raw = sys.argv[2] if len(sys.argv) > 2 else sys.stdin.read()
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
    sys.exit(main())
