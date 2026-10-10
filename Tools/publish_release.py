# -*- coding: utf-8 -*-
"""Create/update a GitHub Release and upload assets via REST API.

The token comes from git credential manager (`git credential fill`) and is
never printed. Reuses an existing Release for the tag when present; same-named
assets are deleted and re-uploaded. Every upload is size-verified against the
local file.

Usage:
  python Tools/publish_release.py --tag v2.54 --title "v2.54 ..." --body-file notes.md \
      --asset XinSpect.exe=publish-254/XinSpect.exe --asset BlueSquadronBridge.exe=... [--repo owner/name]
"""
import argparse, io, json, re, subprocess, sys, urllib.request, urllib.error

def get_token():
    p = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True, timeout=30)
    for line in p.stdout.splitlines():
        m = re.match(r"password=(.+)", line)
        if m:
            return m.group(1).strip()
    sys.exit("ERROR: no token from git credential fill")

def api(url, token, method="GET", payload=None, raw=None, content_type=None):
    data = json.dumps(payload).encode() if payload is not None else raw
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "token " + token)
    if data is not None:
        req.add_header("Content-Type", content_type or "application/json")
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            body = r.read()
            return json.loads(body) if body and "json" in (r.headers.get("Content-Type") or "") else body
    except urllib.error.HTTPError as e:
        print(f"HTTP {e.code} {method} {url}\n{e.read().decode()[:400]}", file=sys.stderr)
        raise

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", required=True)
    ap.add_argument("--title", required=True)
    ap.add_argument("--body-file", required=True)
    ap.add_argument("--asset", action="append", default=[])
    ap.add_argument("--repo", default="Xinglanclever/XinSpect")
    args = ap.parse_args()

    token = get_token()
    base = f"https://api.github.com/repos/{args.repo}"
    body = io.open(args.body_file, encoding="utf-8-sig").read()

    rel = None
    try:
        rel = api(f"{base}/releases/tags/{args.tag}", token)
    except urllib.error.HTTPError:
        pass
    if rel is None:
        rel = api(f"{base}/releases", token, "POST", {
            "tag_name": args.tag, "name": args.title, "body": body,
            "draft": False, "prerelease": False, "target_commitish": "main"})
        print(f"created release id={rel['id']}")
    else:
        rel = api(f"{base}/releases/{rel['id']}", token, "PATCH", {"name": args.title, "body": body})
        print(f"reused release id={rel['id']}")

    existing = {a["name"]: a for a in api(f"{base}/releases/{rel['id']}/assets", token)}
    for spec in args.asset:
        name, path = spec.split("=", 1)
        blob = io.open(path, "rb").read()
        if name in existing:
            api(existing[name]["url"], token, "DELETE")
        up = api(f"https://uploads.github.com/repos/{args.repo}/releases/{rel['id']}/assets?name={name}",
                 token, "POST", raw=blob, content_type="application/octet-stream")
        print(f"uploaded {name}: {up['size']} bytes (local {len(blob)})")
        assert up["size"] == len(blob), f"SIZE MISMATCH for {name}"

    print("DONE")

if __name__ == "__main__":
    main()
