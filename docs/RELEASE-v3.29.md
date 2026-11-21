# XinSpect v3.29

- EDID and CTA decoding now verifies base and extension checksums, preserves partial valid data, and reports malformed data as unknown.
- DMT matching corrects established timing offsets and the Apple 1152x870 mode; detailed timings require a full timing match.
- BCD reads distinguish command failure, missing objects, and absent fields; memory trim units and the global bad-memory object are corrected.
- SPD scans retain completed DIMMs on timeout; custom tool URL and argument identities preserve distinct actions.
- Volume probes no longer depend on shared thread-pool scheduling; XsRegProbe rejects overlapping-buffer address corruption.
- Full regression: 8,132 passed, 0 failed, 0 skipped.
