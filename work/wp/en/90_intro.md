# Appendices

Everything in these appendices is produced by scanning this build's sources with `work/wp_gen.py`:

```
python work/wp_gen.py
```

The generator emits fragments into `work/wp/`, which `work/wp_build.py` (Chinese editions) and `work/wp_build_en.py` (this edition) assemble. The appendices therefore cannot drift from the code: if keys, rules, exemptions or modules change, re-running the generator produces the corresponding appendices and the difference can be compared line by line.

| Appendix | Content | Source |
|---|---|---|
| A | Full static fact-key catalogue with producing modules | `Services/FactKeyCatalog.cs`, `Services/*.cs` |
| B | Dynamic key families and what determines their members | `Services/FactKeyDynamicCatalog.cs` |
| C | Reconciliation rule index | `Services/VerifyRules.cs` |
| D | Explicit exemption list and reasons | `Services/FactCoverageReport.cs` |
| E | Service module index | `Services/*.cs` |
| F | Test file index | `Tests/*.cs` |
| G | Document index | `docs/**/*.md` |
| H | Glossary | Written by hand |

**Note on Appendices B and D in this edition.** The rationale text for dynamic-family declarations and exemptions is authored in the source tree in Chinese, and is reproduced verbatim in the Chinese editions. Appendix B below is a full English translation of all 23 family declarations. Appendix D summarises the exemption categories with counts and translated representative entries; the complete per-entry rationale text for all 562 exemptions is in the Chinese editions (`WHITEPAPER.md`, `WHITEPAPER.zh-CN.md`).

In Appendices A and E, "producing modules" means modules whose source contains the corresponding fact-key string literal. A key may be referenced by several modules (a reader and a presenter, for example), and all referencing modules are listed; a module's "keys owned" is the sum of the keys it references, so those totals exceed the number of keys.

---

### Appendix A — notes

The catalogue holds **844** static fact keys, plus the dynamic families of Appendix B. The prefix table gives, for each of the 107 prefixes, its key count and the modules that produce most of its keys. The per-prefix tables then list every key with the modules that reference it.

---

### Appendix C — notes

There are **18** reconciliation rules. A verdict is one of three — match, conflict, undecidable; no score and no authenticity claim. The scope column records whether a rule runs once per machine or once per disk.

---

### Appendix E — notes, Appendix F — notes and Appendix G — notes

Appendix E indexes the **416** modules under `Services/`, with each module's primary type, line count and the number of fact keys it owns. Appendix F indexes the **433** test files and their `[Fact]`/`[Theory]` marker counts. Appendix G indexes the **51** documents under `docs/` with their titles and byte sizes.

---
