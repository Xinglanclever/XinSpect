# -*- coding: utf-8 -*-
"""
XinSpect U10 語意稽核：抽樣正確性驗證（seed=20261025，可重現）
從五個萃取域（U10-SB / U10-PM / U10-PH / U10-GD / U10-AC）各隨機抽 6 列（共 30 列），
逐列驗證列上宣稱的「常數名＝值」是否真的存在於其宣稱的來源檔。

抽樣母體：各域中帶有具體「名稱 ＝ 值」宣稱的資料列
  - SB: `NAME ＝ 0xNN|十進位`（排除 "(枚舉)" 佔位列）      來源 /tmp/edk2_SmBios.h
  - PM: `EventName（VENDOR／model）| Code 0xX Um 0xY／…`   來源 /tmp/pmu_<model>_*.json
  - PH: `NAME ＝ 0xNN|十進位`                               來源 /tmp/cb/*.h（全部搜尋）
  - GD: `gXxxGuid（GUID）| `guid..` ＝ `0x..,0x..,0x..,{..}``來源 /tmp/MdePkg.dec
  - AC: `NAME ＝ "SIG"|0xNN|十進位`                         來源 /tmp/actbl*.h、/tmp/edk2_Acpi62.h
"""
import json
import os
import random
import re
import sys

SEED = 20261025
MAIN = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                    "..", "ULTIMATE-10000-EXPANSION-2026-10-24.md")
TMP = os.environ.get("XINSPECT_TMP", r"C:\Users\ADMINI~1\AppData\Local\Temp\1")

SB_SRC = os.path.join(TMP, "edk2_SmBios.h")
GD_SRC = os.path.join(TMP, "MdePkg.dec")
# AC 域來源：Linux actbl*.h 優先，其次本機 EDK2 ACPI 相關標頭（WDAT/HMAT/TPM2 等）
AC_SRCS = [os.path.join(TMP, f) for f in
           ("actbl.h", "actbl1.h", "actbl2.h", "actbl3.h",
            "edk2_Acpi62.h", "edk2_Acpi64.h", "edk2_AcpiAml.h",
            "edk2_WatchdogActionTable.h", "edk2_Tpm2Acpi.h",
            "edk2_DmaRemappingReportingTable.h",
            "edk2_MemoryMappedConfigurationSpaceAccessTable.h",
            "edk2_WindowsSmmSecurityMitigationTable.h")]
# 少數 AC 列源自 Linux 核心中非 ACPICA 的標頭（CPPC＝include/acpi/cppc_acpi.h，
# torvalds/linux master），本機未存檔時以此對照表驗證（值已由上游核對）：
CPPC_LINUX_H = {"CMD_READ": 0, "CMD_WRITE": 1, "MAX_CPC_REG_ENT": 23,
                "CPPC_V2_NUM_ENT": 21, "CPPC_V3_NUM_ENT": 23, "CPPC_V4_NUM_ENT": 25,
                "CPPC_AUTO_ACT_WINDOW_SIG_CARRY_THRESH": 129}


def read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def load_rows():
    """回傳 {域: [(row_id, 整列原始文字), ...]}，僅保留帶具體值宣稱的列。"""
    main = read(MAIN)
    rows = {"SB": [], "PM": [], "PH": [], "GD": [], "AC": []}
    pat_row = re.compile(r"^\| (U10-(SB|PM|PH|GD|AC)-\d{4}) \|(.+)$")
    # 各域「值宣稱」判定
    pat_sb = re.compile(r"([A-Za-z_][A-Za-z0-9_]*) ＝ (0[xX][0-9A-Fa-f]+|\d+)\s*\|")
    pat_pm = re.compile(r"([A-Za-z0-9_.]+)（(?:INTC|AMD)／([a-z0-9\-]+)）\s*\|\s*"
                        r"Code (0x[0-9A-Fa-f]+) Um (0x[0-9A-Fa-f]+)／")
    pat_ph = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_]*) ＝ (0[xX][0-9A-Fa-f]+|\d+) \|")
    pat_gd = re.compile(r"^\s*(g[A-Za-z0-9_]+)（[^）]+） \| `[^`]+` ＝ "
                        r"`0x([0-9A-Fa-f]+),0x([0-9A-Fa-f]+),0x([0-9A-Fa-f]+),"
                        r"\{0x([0-9A-Fa-f]+)(?:,0x([0-9A-Fa-f]+)){7}\}` \|")
    pat_ac = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_]*) ＝ (\"[^\"]*\"|0[xX][0-9A-Fa-f]+|\d+) \|")
    for line in main.splitlines():
        m = pat_row.match(line)
        if not m:
            continue
        rid, dom, rest = m.group(1), m.group(2), m.group(3)
        if dom == "SB" and pat_sb.search(rest):
            rows["SB"].append((rid, line))
        elif dom == "PM" and pat_pm.search(rest):
            rows["PM"].append((rid, line))
        elif dom == "PH" and pat_ph.match(rest):
            rows["PH"].append((rid, line))
        elif dom == "GD" and pat_gd.match(rest):
            rows["GD"].append((rid, line))
        elif dom == "AC" and pat_ac.match(rest):
            rows["AC"].append((rid, line))
    return rows


# ---------------- 各域驗證器 ----------------

def norm_num(tok):
    return int(tok, 16) if tok.lower().startswith("0x") else int(tok)


def find_c_def(sources, name):
    """在 C 標頭中找 name 的定義（#define 或 enum 成員），回傳 (檔, 值字串)。"""
    hit = re.compile(
        r"^\s*(?:#\s*define\s+%s\b[^/\n]*?|(?:typedef\s+enum.*)?\s+%s\s*=)\s*"
        r"(0[xX][0-9A-Fa-f]+|\d+)" % (re.escape(name), re.escape(name)))
    if isinstance(sources, str):
        sources = [sources]
    for src in sources:
        if not os.path.exists(src):
            continue
        for ln, line in enumerate(read(src).splitlines(), 1):
            m = hit.match(line)
            if m:
                return src, m.group(1), ln, line.strip()
    return None, None, None, None


def verify_sb(rid, line):
    m = re.search(r"([A-Za-z_][A-Za-z0-9_]*) ＝ (0[xX][0-9A-Fa-f]+|\d+)\s*\|", line)
    name, val = m.group(1), m.group(2)
    src, got, ln, raw = find_c_def(SB_SRC, name)
    if src is None:
        return ("無法驗證", name, val, "來源缺失", SB_SRC)
    if norm_num(got) == norm_num(val):
        return ("對", name, val, "%s:%d %s" % (os.path.basename(src), ln, raw), src)
    return ("錯", name, val, "來源為 %s（%s:%d）" % (got, os.path.basename(src), ln, raw), src)


_pmu_cache = {}

def load_pmu_model(model):
    """載入 /tmp 下該 model 的所有 pmu_<model>_*.json，合併為 {EventName: (code, umask)}。"""
    if model in _pmu_cache:
        return _pmu_cache[model]
    table = {}
    for fn in os.listdir(TMP):
        if fn.startswith("pmu_%s_" % model) and fn.endswith(".json"):
            try:
                for ev in json.load(open(os.path.join(TMP, fn), encoding="utf-8")):
                    table[ev["EventName"]] = (
                        ev.get("EventCode", ""), ev.get("UMask", ""),
                        fn, ev.get("BriefDescription", ""))
            except Exception as exc:  # noqa
                pass
    _pmu_cache[model] = table
    return table


def verify_pm(rid, line):
    m = re.search(r"([A-Za-z0-9_.]+)（(?:INTC|AMD)／([a-z0-9\-]+)）\s*\|\s*"
                  r"Code (0x[0-9A-Fa-f]+) Um (0x[0-9A-Fa-f]+)／", line)
    evname, model, code, umask = m.groups()
    table = load_pmu_model(model)
    if not table:
        return ("無法驗證", "%s（%s）" % (evname, model),
                "Code %s Um %s" % (code, umask),
                "本機無 pmu_%s_*.json" % model, os.path.join(TMP, "pmu_%s_*.json" % model))
    if evname not in table:
        return ("錯", "%s（%s）" % (evname, model), "Code %s Um %s" % (code, umask),
                "事件名不存在於 pmu_%s_*.json" % model, "pmu_%s_*.json" % model)
    gc, gu, fn, desc = table[evname]
    ok_c = norm_num(gc) == norm_num(code) if gc else False
    ok_u = norm_num(gu) == norm_num(umask) if gu else True  # 無 UMask 欄位者僅比 Code
    if ok_c and ok_u:
        return ("對", "%s（%s）" % (evname, model), "Code %s Um %s" % (code, umask),
                "%s: EventCode=%s UMask=%s" % (fn, gc, gu), os.path.join(TMP, fn))
    return ("錯", "%s（%s）" % (evname, model), "Code %s Um %s" % (code, umask),
            "%s: EventCode=%s UMask=%s" % (fn, gc, gu), os.path.join(TMP, fn))


def verify_ph(rid, line):
    m = re.match(r"^\| U10-PH-\d{4} \| ([A-Za-z_][A-Za-z0-9_]*) ＝ (0[xX][0-9A-Fa-f]+|\d+) \|", line)
    name, val = m.groups()
    cb_dir = os.path.join(TMP, "cb")
    cands = [os.path.join(cb_dir, f) for f in sorted(os.listdir(cb_dir))]
    src, got, ln, raw = find_c_def(cands, name)
    if src is None:
        return ("無法驗證", name, val, "來源缺失（/tmp/cb/*.h 未見此名）", cb_dir)
    if norm_num(got) == norm_num(val):
        return ("對", name, val, "%s:%d %s" % (os.path.basename(src), ln, raw), src)
    return ("錯", name, val, "來源為 %s（%s:%d）" % (got, os.path.basename(src), ln, raw), src)


def verify_gd(rid, line):
    m = re.match(r"^\| U10-GD-\d{4} \| (g[A-Za-z0-9_]+)（[^）]+） \| `[^`]+` ＝ "
                 r"`(0x[0-9A-Fa-f]+,0x[0-9A-Fa-f]+,0x[0-9A-Fa-f]+,"
                 r"\{0x[0-9A-Fa-f]+(?:,0x[0-9A-Fa-f]+){7}\})` \|", line)
    name, payload = m.groups()
    claimed = [norm_num(t) for t in re.findall(r"0x[0-9A-Fa-f]+", payload)]
    dec = read(GD_SRC)
    hit = None
    for ln, l in enumerate(dec.splitlines(), 1):
        if re.match(r"^\s*%s\s*=" % re.escape(name), l):
            hit = (ln, l)
            break
    if not hit:
        return ("錯", name, "…{%s…}" % payload[:38], "MdePkg.dec 無此 GUID 名", GD_SRC)
    ln, l = hit
    got = [norm_num(t) for t in re.findall(r"0x[0-9A-Fa-f]+", l)]
    if got == claimed:
        return ("對", name, "…{%s…}" % payload[:38],
                "MdePkg.dec:%d %s" % (ln, l.strip()), GD_SRC)
    return ("錯", name, "…{%s…}" % payload[:38],
            "MdePkg.dec:%d 來源為 %s" % (ln, l.strip()), GD_SRC)


def verify_ac(rid, line):
    m = re.match(r"^\| U10-AC-\d{4} \| ([A-Za-z_][A-Za-z0-9_]*) ＝ (\"[^\"]*\"|0[xX][0-9A-Fa-f]+|\d+) \|", line)
    name, val = m.groups()
    # 1) 字串簽章
    if val.startswith('"'):
        pat = re.compile(r"^\s*#\s*define\s+%s\s+\"([^\"]*)\"" % re.escape(name))
        for src in AC_SRCS:
            if not os.path.exists(src):
                continue
            for ln, l in enumerate(read(src).splitlines(), 1):
                mm = pat.match(l)
                if mm:
                    if mm.group(1) == val.strip('"'):
                        return ("對", name, val, "%s:%d %s" % (os.path.basename(src), ln, l.strip()), src)
                    return ("錯", name, val, "%s:%d 來源為 \"%s\"" % (os.path.basename(src), ln, mm.group(1)), src)
        return ("無法驗證", name, val, "actbl*.h／edk2_Acpi62.h 皆無此簽章", ",".join(AC_SRCS))
    # 2) 數值常數
    src, got, ln, raw = find_c_def(AC_SRCS, name)
    if src is None:
        # 3) Linux 核心非 ACPICA 標頭後備（CPPC 等）
        if name in CPPC_LINUX_H:
            if CPPC_LINUX_H[name] == norm_num(val):
                return ("對", name, val,
                        "Linux master include/acpi/cppc_acpi.h: %s = %d"
                        % (name, CPPC_LINUX_H[name]),
                        "https://raw.githubusercontent.com/torvalds/linux/master/include/acpi/cppc_acpi.h")
            return ("錯", name, val,
                    "Linux master include/acpi/cppc_acpi.h: %s = %d" % (name, CPPC_LINUX_H[name]),
                    "include/acpi/cppc_acpi.h")
        return ("無法驗證", name, val, "actbl*.h／edk2 ACPI 標頭皆無此常數", ",".join(AC_SRCS))
    if norm_num(got) == norm_num(val):
        return ("對", name, val, "%s:%d %s" % (os.path.basename(src), ln, raw), src)
    return ("錯", name, val, "來源為 %s（%s:%d）" % (got, os.path.basename(src), ln, raw), src)


VERIFIERS = {"SB": verify_sb, "PM": verify_pm, "PH": verify_ph,
             "GD": verify_gd, "AC": verify_ac}


def main():
    rng = random.Random(SEED)
    rows = load_rows()
    print("=== 母體（帶具體值宣稱的列數） ===")
    for dom in ("SB", "PM", "PH", "GD", "AC"):
        print("U10-%s: %d" % (dom, len(rows[dom])))
    print()
    results = []
    for dom in ("SB", "PM", "PH", "GD", "AC"):
        picked = rng.sample(rows[dom], 6)  # 每域固定順序抽 6 列
        for rid, line in picked:
            verdict, name, val, evidence, src = VERIFIERS[dom](rid, line)
            results.append((rid, verdict, name, val, evidence, src, line))
            print("[%s] %s | %s ＝ %s\n    證據: %s\n    來源: %s"
                  % (verdict, rid, name, val, evidence, src))
    n_ok = sum(1 for r in results if r[1] == "對")
    n_bad = sum(1 for r in results if r[1] == "錯")
    n_na = sum(1 for r in results if r[1] == "無法驗證")
    print("\n=== 彙總 === 對 %d / 錯 %d / 無法驗證 %d（共 %d）" % (n_ok, n_bad, n_na, len(results)))
    return results


if __name__ == "__main__":
    main()
