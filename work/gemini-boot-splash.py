```python
import sys
import time

ESC = "\033["
RESET = f"{ESC}0m"
BOLD = f"{ESC}1m"
DIM = f"{ESC}2m"

# Colors (Swiss / Industrial palette)
C_RED = f"{ESC}38;2;226;35;26m"      # Swiss Red #E2231A
C_BG_RED = f"{ESC}48;2;226;35;26m"
C_WHITE = f"{ESC}38;2;242;240;234m"   # Paper White #F2F0EA
C_GRAY = f"{ESC}38;2;110;110;110m"
C_DARK = f"{ESC}38;2;45;45;45m"
C_AMBER = f"{ESC}38;2;215;153;33m"
C_GREEN = f"{ESC}38;2;50;180;80m"
BG_BLACK = f"{ESC}48;2;12;12;12m"

W = 78

def pad(s, width=W):
    return s

lines = [
    # 1. Header Box Top
    f"{C_DARK}�z{'�w' * 76}�{{RESET}",
    
    # 2-8. Banner with ASCII art & side badges
    f"{C_DARK}�x {C_WHITE}{BOLD}�i�i��  �i�i�ߢi�i�ߢi�i�i��   �i�i��{C_DARK}�x{C_WHITE} v3.16-rel      {C_DARK}�x {C_BG_RED}{C_WHITE}{BOLD} ��Ū?���g�w��?���s�� {RESET}{C_DARK}         �x{RESET}",
    f"{C_DARK}�x {C_WHITE}{BOLD}��i�i�ߢi�i����i�i���i�i�i�i��  �i�i��{C_DARK}�u�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�r�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�t{RESET}",
    f"{C_DARK}�x {C_WHITE}{BOLD} ��i�i�i���� �i�i���i�i�ݢi�i�� �i�i��{C_DARK}�x{C_GRAY} TELEMETRY ENGINE : {C_WHITE}Ring0 / MSR / WMI / PCI-Direct  {C_DARK}�x{RESET}",
    f"{C_DARK}�x {C_WHITE}{BOLD} �i�i�ݢi�i�� �i�i���i�i����i�i�ߢi�i��{C_DARK}�x{C_GRAY} ARCH POSTURE     : {C_WHITE}x86_64 Strict Read-Only Policy  {C_DARK}�x{RESET}",
    f"{C_DARK}�x {C_WHITE}{BOLD}�i�i���� �i�i�ߢi�i���i�i�� ��i�i�i�i��{C_DARK}�x{C_GRAY} TARGET BUS       : {C_WHITE}Domain 0000:00-FF [Direct MAP]  {C_DARK}�x{RESET}",
    f"{C_DARK}�x {C_WHITE}{BOLD}������  ������������������  ����������{C_DARK}�x{C_GRAY} PIPE BRIDGE      : {C_WHITE}\\.\\pipe\\XinSpect.BlueSquadron  {C_DARK}�x{RESET}",
    f"{C_DARK}�u�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�r�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�t{RESET}",

    # 9. Split Column Header
    f"{C_DARK}�x {C_WHITE}{BOLD}PROBE / SUBSYSTEM TRACE{C_DARK}                            �x {C_WHITE}{BOLD}HIST / TELEMETRY METRICS{C_DARK}  �x{RESET}",
    f"{C_DARK}�u�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�q�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�t{RESET}",

    # 10-18. Dual pane: OK/-- Probe traces + Metrics & Histograms
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}pci.enum: Bus 00:00.0 Host Bridge    {C_DARK}�x {C_GRAY}LHM Core{C_DARK}: {C_WHITE}38�XC {C_DARK} �d�f�h??? �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}msr.ia32_therm_status: MSR 0x19C VAL{C_DARK}�x {C_GRAY}PCI Tput{C_DARK}: {C_WHITE}12.4G{C_DARK} �i�h�f�d   �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}wmi.root_wmi: ThermalZoneReported    {C_DARK}�x {C_GRAY}MSR Read{C_DARK}: {C_WHITE}142us{C_DARK} �c�d�c�c?? �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GRAY}  --  {C_DARK}] {C_GRAY}ring0.kdrv: Physical BAR unmapped   {C_DARK}�x {C_GRAY}rf.drift{C_DARK}: {C_DARK}[ -- ] ????? �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}lhm.sysinfo: SMBIOS Type 4 Raw 128B  {C_DARK}�x {C_GRAY}Ring0 IO{C_DARK}: {C_WHITE}LOCK {C_DARK} �i��???? �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}pci.cfg: 00:01.0 PEG Root Port Link  {C_DARK}�x {C_GRAY}Bus Polr{C_DARK}: {C_WHITE}64-b {C_DARK} �h�h�g�f�d  �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GRAY}  --  {C_DARK}] {C_GRAY}rf.node_sensor: SMBus ARA timed out {C_DARK}�x {C_GRAY}Sys Jitr{C_DARK}: {C_WHITE}0.8ns{C_DARK} �c      �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}msr.pkg_power_info: RAPL Unit Config {C_DARK}�x {C_GRAY}Ops Conf{C_DARK}: {C_WHITE}SYNC {C_DARK} ��������?? �x{RESET}",
    f"{C_DARK}�x {C_DARK}[{C_GREEN}  OK  {C_DARK}] {C_WHITE}sec.posture: Kernel RW Disabled [ENF]{C_DARK}�x {C_GRAY}Heap Pk {C_DARK}: {C_WHITE}4.2M {C_DARK} �e�f�d�c   �x{RESET}",

    # 19. Divider
    f"{C_DARK}�u�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�r�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�t{RESET}",

    # 20-22. Ops Queue & State Check
    f"{C_DARK}�x {C_GRAY}OPS: {C_WHITE}0x101:MSR_AUDIT{C_DARK} �w> {C_WHITE}0x102:PCI_BAR_CHECK{C_DARK} �w> {C_WHITE}0x103:LHM_CACHE_FLUSH{C_DARK} �w> {C_AMBER}READY{C_DARK} �x{RESET}",
    f"{C_DARK}�x {C_GRAY}MUTATION INTERCEPT: {C_WHITE}0 WRITES ISSUED {C_DARK}�x {C_GRAY}OUTBOUND NET: {C_WHITE}SOCKET_CREATE BLOCKED{C_DARK}      �x{RESET}",
    f"{C_DARK}�x {C_GRAY}COLD START SYNC: 24 SENSORS BOUND, 2 INACTIVE (FACT CONFIRMED)           {C_DARK}�x{RESET}",

    # 23-24. Progress Bar
    f"{C_DARK}�u�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�t{RESET}",
    f"{C_DARK}�x {C_WHITE}{BOLD}BOOT PROGRESS{C_RESET} {C_DARK}[{C_WHITE}�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i�i{C_DARK}] {C_WHITE}{BOLD}100.0%{C_DARK} �x{RESET}",

    # 25-26. Bottom Status Bar & Frame Bottom
    f"{C_DARK}�u�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�w�t{RESET}",
    f"{C_DARK}�x {C_RED}��{C_WHITE}{BOLD} READY ? ��Ū�Ҧ�{RESET}{' ' * 27}{C_GRAY}xsc v3.16 ? �f�� XinSpect Console {C_DARK}�x{RESET}",
    f"{C_DARK}�|{'�w' * 76}�}{RESET}"
]

def main():
    sys.stdout.write(f"{ESC}?25l{ESC}2J{ESC}H")
    sys.stdout.flush()
    for line in lines:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()
        time.sleep(0.04)
    sys.stdout.write(f"{ESC}?25h")
    sys.stdout.flush()

if __name__ == "__main__":
    main()

```
