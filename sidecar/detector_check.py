#!/usr/bin/env python3
"""Run real fingerprint-detector sites through Camoufox and report what leaks.

Use this to check a profile against live detectors with YOUR network/VPN (e.g. HMA).

Two ways to route traffic:
  A) HMA as a system VPN (whole machine): just connect HMA, then run WITHOUT --proxy.
     Traffic (and Camoufox's own geoip lookup) goes through HMA automatically.
  B) HMA (or any proxy) as an explicit endpoint: pass --proxy.
     Examples:
       --proxy http://user:pass@host:port
       --proxy socks5://user:pass@host:port

Usage:
  python detector_check.py                      # system VPN / direct, claim Windows
  python detector_check.py --os macos
  python detector_check.py --proxy socks5://u:p@1.2.3.4:1080
  python detector_check.py --headful            # open a real window (more realistic than headless)

Needs the camoufox package: pip install "camoufox[geoip]"
Screenshots + a summary are written to ./detector-report/.
"""
import argparse
import json
import os
import re
import time

from camoufox.sync_api import Camoufox

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "detector-report")


def parse_proxy(s):
    if not s:
        return None
    m = re.match(r"(?P<scheme>\w+)://(?:(?P<user>[^:]+):(?P<pw>[^@]+)@)?(?P<host>[^:/]+):(?P<port>\d+)", s)
    if not m:
        raise SystemExit(f"Bad --proxy: {s}")
    d = m.groupdict()
    proxy = {"server": f"{d['scheme']}://{d['host']}:{d['port']}"}
    if d["user"]:
        proxy["username"] = d["user"]
        proxy["password"] = d["pw"]
    return proxy


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--os", default="windows", choices=["windows", "macos", "linux"])
    ap.add_argument("--proxy", default=None)
    ap.add_argument("--headful", action="store_true")
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    proxy = parse_proxy(args.proxy)

    # geoip=True -> timezone/geo/locale follow the exit IP (proxy or system VPN), never the host clock.
    launch = dict(os=args.os, headless=not args.headful, geoip=True, humanize=True)
    if proxy:
        launch["proxy"] = proxy

    summary = {}
    with Camoufox(**launch) as b:
        page = b.new_page()

        # 1) What IP / timezone does the world see?
        page.goto("https://ip-api.com/json", wait_until="load", timeout=60000)
        try:
            ipinfo = json.loads(page.evaluate("() => document.body.innerText"))
            summary["public_ip"] = ipinfo.get("query")
            summary["ip_country"] = ipinfo.get("country")
            summary["ip_tz"] = ipinfo.get("timezone")
        except Exception as e:
            summary["ip_error"] = str(e)
        summary["browser_tz"] = page.evaluate("() => Intl.DateTimeFormat().resolvedOptions().timeZone")
        summary["platform"] = page.evaluate("() => navigator.platform")

        # 2) CreepJS (detailed) — let it compute, then screenshot + pull headline lines.
        page.goto("https://abrahamjuliot.github.io/creepjs/", wait_until="load", timeout=60000)
        time.sleep(16)
        page.screenshot(path=os.path.join(OUT, "creepjs.png"), full_page=True)
        txt = page.evaluate("() => document.body.innerText")
        summary["creepjs_lines"] = [
            l.strip() for l in txt.splitlines()
            if any(k in l.lower() for k in ["trust", "lies (", "bot", "stealth", "headless", "resistance"])
        ][:20]

        # 3) iphey — quick verdict.
        try:
            page.goto("https://iphey.com/", wait_until="load", timeout=60000)
            time.sleep(10)
            page.screenshot(path=os.path.join(OUT, "iphey.png"), full_page=True)
        except Exception as e:
            summary["iphey_error"] = str(e)

        # 4) browserleaks canvas/webgl (screenshots for manual inspection).
        for name, url in [("canvas", "https://browserleaks.com/canvas"),
                          ("webgl", "https://browserleaks.com/webgl"),
                          ("webrtc", "https://browserleaks.com/webrtc")]:
            try:
                page.goto(url, wait_until="load", timeout=60000)
                time.sleep(6)
                page.screenshot(path=os.path.join(OUT, f"browserleaks-{name}.png"), full_page=True)
            except Exception as e:
                summary[f"{name}_error"] = str(e)

    with open(os.path.join(OUT, "summary.json"), "w") as f:
        json.dump(summary, f, indent=2, ensure_ascii=False)
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    print(f"\nScreenshots + summary.json in: {OUT}")


if __name__ == "__main__":
    main()
