#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把一版發佈的四件資產同步到鏡像（Google Drive／123 雲盤），並對帳。

為什麼要有這支：每一次發版，GitHub Release 是一次 API 呼叫就完成，但 Drive 與
123 雲盤只能手動上傳——兩邊各自要開瀏覽器、拖檔案、等上傳、複製連結。發一版要
做三次，而且做完還不確定三邊的檔案是不是同一份。

這裡的做法把「同步」變成兩個可以重複執行的動作：

  ① 就地覆寫：每個網盤只有一個固定資料夾 `<root>/latest/`，每次發版把同名檔案
     蓋掉。分享連結因此**永遠不變**，Installer/mirrors.txt 不用改，安裝器 exe
     也不用重編（它在執行時抓 mirrors.txt）。

  ② 逐檔對帳：上傳完（或任何時候）跑 --check，比對遠端與本地四件資產的檔名、
     大小與雜湊。不一致就回非零——「同步」是驗出來的，不是上傳完自己宣布的。

兩個網盤**同時**上傳（各自一個執行緒），不必等一個跑完再跑另一個。

用法：
    python Tools/sync-mirrors.py --check                 # 只對帳（發版後、或想確認時）
    python Tools/sync-mirrors.py --sync                  # 上傳並對帳
    python Tools/sync-mirrors.py --sync --version 3.08   # 指定版本（預設讀 XinSpect.csproj）
    python Tools/sync-mirrors.py --sync --source release # 不在發版機器上時，從 GitHub 抓資產
    python Tools/sync-mirrors.py --update-mirrors        # 依 Tools/mirrors.json 重寫 mirrors.txt

前置：rclone（https://rclone.org/downloads/），兩個 remote 設定見 docs/MIRRORS.md。
沒有 rclone 時只有 --dry-run 能跑——它會把完整計畫印出來，什麼都不上傳。
"""

import argparse
import concurrent.futures
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG_PATH = os.path.join(ROOT, "Tools", "mirrors.json")
MIRRORS_TXT = os.path.join(ROOT, "Installer", "mirrors.txt")
REPO = "Xinglanclever/XinSpect"

# 要同步的檔案。順序不重要，但四件都必須有——少一件鏡像就是半套。
ASSETS = ["XinSpect.exe", "BlueSquadronBridge.exe", "XinSpectDeploy.exe", "SHA256SUMS.txt"]


def say(msg):
    print(msg, flush=True)


def die(msg):
    print("錯誤：" + msg, file=sys.stderr, flush=True)
    raise SystemExit(1)


# ---------------------------------------------------------------- 版本與來源


def project_version():
    path = os.path.join(ROOT, "XinSpect.csproj")
    with open(path, encoding="utf-8") as f:
        m = re.search(r"<Version>([0-9]+\.[0-9]+[0-9.]*)</Version>", f.read())
    if not m:
        die("XinSpect.csproj 裡找不到 <Version>")
    return m.group(1)


def local_asset_paths(version):
    """發版機器上的路徑。找不到就是找不到，不猜。"""
    sums = [os.path.join(tempfile.gettempdir(), "SHA256SUMS-%s.txt" % version),
            os.path.join(ROOT, "obj", "SHA256SUMS-%s.txt" % version)]
    return {
        "XinSpect.exe": os.path.join(ROOT, "publish-%s" % version, "XinSpect.exe"),
        "BlueSquadronBridge.exe": os.path.join(ROOT, "publish-2.44-bridge", "BlueSquadronBridge.exe"),
        "XinSpectDeploy.exe": os.path.join(ROOT, "Installer", "XinSpectDeploy.exe"),
        "SHA256SUMS.txt": next((p for p in sums if os.path.exists(p)), sums[0]),
    }


def _download(url, dest):
    """curl 優先：這台機器上 urllib 對 GitHub 會間歇性斷線，curl 不會。"""
    if shutil.which("curl"):
        last = ""
        for _ in range(4):
            r = subprocess.run(["curl", "-sSL", "--fail", "--retry", "3", "-o", dest, url],
                               capture_output=True, text=True)
            if r.returncode == 0 and os.path.exists(dest) and os.path.getsize(dest) > 0:
                return
            last = (r.stderr or "").strip()[:160]
        die("下載失敗：%s（%s）" % (url, last))
    for _ in range(3):
        try:
            with urllib.request.urlopen(url, timeout=60) as resp, open(dest, "wb") as f:
                shutil.copyfileobj(resp, f)
            return
        except Exception as e:  # noqa: BLE001 - 網路錯誤種類很多，統一重試
            last = str(e)[:160]
    die("下載失敗：%s（%s）" % (url, last))


def release_asset_paths(version, workdir):
    """從 GitHub Release 抓四件資產（不在發版機器上時用）。"""
    os.makedirs(workdir, exist_ok=True)
    out = {}
    for name in ASSETS:
        dest = os.path.join(workdir, name)
        if os.path.exists(dest) and os.path.getsize(dest) > 0:
            out[name] = dest
            continue
        url = "https://github.com/%s/releases/download/v%s/%s" % (REPO, version, name)
        say("  下載 v%s/%s …" % (version, name))
        _download(url, dest)
        out[name] = dest
    return out


def resolve_assets(args, version):
    if args.source == "release":
        return release_asset_paths(version, os.path.join(ROOT, "obj", "_mirror_%s" % version))
    paths = local_asset_paths(version)
    missing = [n for n, p in paths.items() if not os.path.exists(p)]
    if missing and args.source == "auto":
        say("本地缺 %s，改從 GitHub Release 抓。" % "、".join(missing))
        return release_asset_paths(version, os.path.join(ROOT, "obj", "_mirror_%s" % version))
    if missing:
        die("本地找不到：%s（先發版，或用 --source release）" % "、".join(missing))
    return paths


# ---------------------------------------------------------------- 設定檔


def load_config():
    if not os.path.exists(CONFIG_PATH):
        die("沒有 %s——先照 docs/MIRRORS.md 建立（一次）" % os.path.relpath(CONFIG_PATH, ROOT))
    with open(CONFIG_PATH, encoding="utf-8") as f:
        cfg = json.load(f)
    providers = cfg.get("providers") or {}
    if not providers:
        die("%s 裡沒有 providers" % os.path.relpath(CONFIG_PATH, ROOT))
    for name, p in providers.items():
        if not p.get("enabled", True):
            continue  # 還沒接上的 provider 只留名字與說明，不強迫填 remote
        for key in ("remote", "path"):
            if not p.get(key):
                die("provider %s 缺 %s" % (name, key))
    return cfg


def rclone_bin():
    exe = os.path.join(ROOT, "Tools", "bin", "rclone.exe")
    if os.path.exists(exe):
        return exe
    found = shutil.which("rclone")
    if found:
        return found
    return None


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- 上傳與對帳


def remote_target(provider, name):
    remote = provider["remote"].rstrip("/")
    if not remote.endswith(":"):
        remote += ":"  # gdrive: ＋ XinSpect/latest；使用者寫成 gdrive 也接受
    return "%s%s/%s" % (remote, provider["path"].strip("/"), name)


def human(n):
    if n < 1024:
        return "%d bytes" % n
    if n < 1048576:
        return "%.0f KB" % (n / 1024.0)
    return "%.1f MB" % (n / 1048576.0)


def upload_one(rclone, provider, version, assets, dry):
    """一個網盤：逐檔就地覆寫，然後對帳。回傳 (ok, 說明)。"""
    label = provider.get("label") or "鏡像"
    if not dry:
        # copyto 不會替你建出深層資料夾（第一次對著全新網盤上傳就是「directory not found」）。
        base = provider["remote"].rstrip("/")
        if not base.endswith(":"):
            base += ":"
        r = subprocess.run([rclone, "mkdir", "%s%s" % (base, provider["path"].strip("/"))],
                           capture_output=True, text=True)
        if r.returncode != 0:
            return False, "[%s] 建資料夾失敗：%s（%s）" % (
                label, provider["path"], (r.stderr or "").strip().splitlines()[-1][:160] if r.stderr else "")
    for name in ASSETS:
        dst = remote_target(provider, name)
        if dry:
            say("  [%s] 會上傳 %s（%s）→ %s" % (label, name, human(os.path.getsize(assets[name])), dst))
            continue
        # --checksum：同名檔案的內容不同才重傳；大小與雜湊都一樣就跳過。
        r = subprocess.run([rclone, "copyto", "--checksum", "--stats-one-line", "-v",
                            assets[name], dst], capture_output=True, text=True)
        if r.returncode != 0:
            return False, "[%s] 上傳失敗：%s（%s）" % (label, name, (r.stderr or "").strip()[-200:])
    if dry:
        return True, "[%s] （dry-run，未上傳）" % label

    drifted = verify_provider(rclone, provider, assets)
    if drifted:
        return False, "[%s] 上傳後對帳失敗：%s" % (label, "；".join(drifted))
    return True, "[%s] %d 件一致（%s/）" % (label, len(ASSETS), provider["path"].strip("/"))


def verify_provider(rclone, provider, assets):
    """逐檔比對大小與雜湊。回傳不一致的說明清單（空的＝一致）。"""
    problems = []
    for name in ASSETS:
        dst = remote_target(provider, name)
        r = subprocess.run([rclone, "lsjson", "--hash", dst], capture_output=True, text=True)
        if r.returncode != 0 or not (r.stdout or "").strip():
            problems.append("%s 遠端不存在" % name)
            continue
        try:
            meta = json.loads(r.stdout)
        except json.JSONDecodeError:
            problems.append("%s 讀不到遠端資訊" % name)
            continue
        if isinstance(meta, list):  # lsjson 對檔案有時也回單元素陣列
            meta = meta[0] if meta else {}
        if not meta:
            problems.append("%s 遠端查不到" % name)
            continue
        local_size = os.path.getsize(assets[name])
        if meta.get("Size") != local_size:
            problems.append("%s 大小不符（遠端 %s ≠ 本地 %d）" % (name, meta.get("Size"), local_size))
            continue
        remote_sha = (meta.get("Hashes") or {}).get("SHA-256")
        if not remote_sha:
            # 很多後端不在 lsjson 裡回 SHA-256（本地與 Drive 回 MD5／QuickXor），但
            # `rclone hashsum` 可以另外要——能算就算，別直接退成只比大小。
            h = subprocess.run([rclone, "hashsum", "SHA256", dst], capture_output=True, text=True)
            if h.returncode == 0 and (h.stdout or "").strip():
                remote_sha = h.stdout.strip().split()[0]
        if remote_sha:
            local_sha = sha256(assets[name])
            if remote_sha.lower() != local_sha:
                problems.append("%s 雜湊不符（遠端 %s… ≠ 本地 %s…）" % (name, remote_sha[:12], local_sha[:12]))
            continue
        # Drive 不提供 SHA-256（它用 MD5／QuickXorHash）。大小一致、且上傳剛跑過
        # --checksum，仍可能有極少數情況說謊，所以把「只有大小可比」講明白。
        problems.append("%s 只有大小可比（該網盤不回 SHA-256，已比大小 %d bytes）" % (name, local_size))
    # 「只有大小可比」不是不一致，單獨回報但不算失敗
    hard = [p for p in problems if "只有大小可比" not in p]
    soft = [p for p in problems if "只有大小可比" in p]
    if soft:
        say("  注意：%s" % "；".join(soft))
    return hard


def run_sync(cfg, version, assets, dry):
    rclone = rclone_bin()
    if rclone is None and not dry:
        die("找不到 rclone——裝法見 docs/MIRRORS.md；只想看計畫的話加 --dry-run")

    enabled = [(n, p) for n, p in cfg["providers"].items() if p.get("enabled", True)]
    if not enabled:
        die("Tools/mirrors.json 裡沒有啟用的 provider——照 docs/MIRRORS.md 把要用的設成 enabled: true")

    say("同步 v%s → %s" % (version, "、".join(p.get("label") or n for n, p in enabled)))
    for name in ASSETS:
        say("  %-24s %10d bytes  %s…" % (name, os.path.getsize(assets[name]), sha256(assets[name])[:12]))

    # 兩個網盤同時跑：一個的 6 MB 上傳不該擋住另一個的 37 MB。
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(enabled)) as pool:
        futures = {pool.submit(upload_one, rclone, p, version, assets, dry): (n, p) for n, p in enabled}
        for fut in concurrent.futures.as_completed(futures):
            name, prov = futures[fut]
            try:
                results.append(fut.result())
            except Exception as e:  # noqa: BLE001 - 單一網盤炸掉不該讓另一個的結果消失
                results.append((False, "[%s] 例外：%s" % (prov.get("label") or name, e)))

    ok = True
    for good, msg in results:
        say(("  ✓ " if good else "  ✗ ") + msg)
        ok = ok and good
    return ok


def run_check(cfg, assets):
    rclone = rclone_bin()
    if rclone is None:
        die("對帳需要 rclone（--dry-run 不需要）")
    enabled = [(n, p) for n, p in cfg["providers"].items() if p.get("enabled", True)]
    if not enabled:
        # 一家都沒啟用卻回「對帳完成、退出碼 0」，就是什麼都沒驗卻宣稱驗過了。
        die("Tools/mirrors.json 裡沒有啟用的 provider——照 docs/MIRRORS.md 把要用的設成 enabled: true")
    say("對帳四件資產（%s）" % "、".join(p.get("label") or n for n, p in enabled))
    ok = True
    for name, prov in enabled:
        drifted = verify_provider(rclone, prov, assets)
        label = prov.get("label") or name
        if drifted:
            ok = False
            say("  ✗ [%s] 不一致：%s" % (label, "；".join(drifted)))
        else:
            say("  ✓ [%s] 與本地一致" % label)
    return ok


# ---------------------------------------------------------------- mirrors.txt


def run_update_mirrors(cfg):
    """依設定檔重寫 mirrors.txt 的 2／3 兩格。分享連結不變就不要跑這個。"""
    wanted = {}
    for key, prov, default_mode in ((2, "gdrive", "page"), (3, "chinadirect", "direct")):
        p = cfg["providers"].get(prov) or {}
        if p.get("enabled", True) and p.get("share"):
            wanted[key] = (p["share"], p.get("mode", default_mode))
    if not wanted:
        die("設定檔裡沒有可寫進 mirrors.txt 的 share（gdrive／chinadirect 要 enabled 且有 share）")

    with open(MIRRORS_TXT, encoding="utf-8") as f:
        text = f.read()

    out = []
    inserted = False
    for line in text.split("\n"):
        s = line.strip()
        m = re.match(r"^([23])\|", s)
        if m:  # 舊的啟用行一律拿掉，稍後重寫
            continue
        out.append(line)
        if not inserted and s.startswith("1|"):
            for key in sorted(wanted):
                out.append("%d|%s|%s" % (key, wanted[key][0], wanted[key][1]))
            inserted = True

    if not inserted:  # 連 1| 都沒有：接在檔尾，不要憑空猜位置
        for key in sorted(wanted):
            out.append("%d|%s|%s" % (key, wanted[key][0], wanted[key][1]))

    with open(MIRRORS_TXT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))
    for key in sorted(wanted):
        say("  %d|%s|%s" % (key, wanted[key][0], wanted[key][1]))
    say("已重寫 %s——記得跟程式一起提交（安裝器執行時才抓得到）。" % os.path.relpath(MIRRORS_TXT, ROOT))
    return True


# ---------------------------------------------------------------- main


def main():
    global CONFIG_PATH
    ap = argparse.ArgumentParser(description="把一版資產同步到鏡像並對帳")
    ap.add_argument("--version", default="", help="預設讀 XinSpect.csproj 的 <Version>")
    ap.add_argument("--config", default=CONFIG_PATH)
    ap.add_argument("--source", choices=["auto", "local", "release"], default="auto",
                    help="資產來源；auto＝本地有就用，缺的從 Release 抓")
    ap.add_argument("--sync", action="store_true", help="上傳並對帳")
    ap.add_argument("--check", action="store_true", help="只對帳")
    ap.add_argument("--update-mirrors", action="store_true", help="依設定檔重寫 Installer/mirrors.txt")
    ap.add_argument("--dry-run", action="store_true", help="只印計畫，不上傳")
    args = ap.parse_args()

    CONFIG_PATH = args.config

    if not (args.sync or args.check or args.update_mirrors):
        die("要指定 --sync、--check 或 --update-mirrors（--dry-run 要搭配 --sync）")

    version = args.version or project_version()
    cfg = load_config()

    if args.update_mirrors:
        return 0 if run_update_mirrors(cfg) else 1

    assets = resolve_assets(args, version)
    if args.sync:
        return 0 if run_sync(cfg, version, assets, args.dry_run) else 1
    return 0 if run_check(cfg, assets) else 1


if __name__ == "__main__":
    raise SystemExit(main())
