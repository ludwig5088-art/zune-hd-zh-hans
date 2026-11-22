Zune HD Simplified Chinese handheld localization 0.2.3
Experimental community source release. Upstream approval and catalog inclusion are pending.

Project: https://github.com/ludwig5088-art/zune-hd-zh-hans
License: MIT (see LICENSE).

This source export reproduces the data-only mod tested on one retail Zune HD
4.5 (114) with Project Lyra 1.4.0. It replaces 103 Spanish tables across eight
GEM archives. Select Espanol to display Chinese; English remains available.
It does not modify firmware binaries, fonts, or the two TV-specific archives.

Contributors and AI models
ludwig5088-art provided project direction, wording decisions and physical
device testing. Astra / OpenAI Codex handled primary implementation and
verification using GPT-6.
Claude / Anthropic Claude Opus 5.5 (claude-opus-5-5), accessed through Claude Code,
provided independent design/code reviews and troubleshooting analysis.
Twelve completed Claude review records confirm that response model identifier.
See CREDITS.txt and AI-PROVENANCE.json for roles and evidence scope.

Contents
- translations.json: translated text and reconstruction recipes. Unchanged
  stock text is read from the owner's input files, not copied into this export.
- rebuild.py: offline, standard-library-only Python 3.10+ builder.
- manifest.json and expected-files.json: exact tested manifest and member hashes.
- installer/: unchanged C# source and pinned specification of the tested Windows
  installer. The generated .zmod is supplied locally when compiling it.
- validation-summary.json and RECOVERY.txt: evidence scope and recovery steps.
- UPSTREAM-REVIEW.txt: request for Project Lyra maintainer review.
- LICENSE: MIT terms for the contributed source and translations.

Build without a device or network connection
Provide unmodified retail 4.5 (114) GEM exports, not Lyra-composed runtime files:
  python rebuild.py --stock-dir /path/to/stock-gems --out /path/to/new-build
The required filenames and exact hashes are in translations.json. Alternatively,
the private preservation ZIP is supported with --stock-zip and --zip-prefix.
The builder validates all inputs before writing, refuses an existing output
directory, and checks all 104 output members against the tested package.
It produces new-build/mod/ and new-build/zune-hd-zh-hans-0.2.3.zmod.
ZIP compression can differ between zlib versions; member hashes are authoritative.
The optional existing Windows installer also pins the exact original ZIP hash.
See installer/build.ps1; no hash guard should be bypassed for a different bundle.

Upstream structural validation (offline; use a verified Lyra 1.4.0 checkout):
  python -X utf8 /path/to/project-lyra/modkit/mod-apply.py validate /path/to/new-build/mod
Do not mistake structural validation for firmware/platform compatibility checks.
In particular, capability matching does not prove that another Lyra version's
string ID layout matches the 1.4.0-specific five compatibility labels.

Known limits and release decisions
- Only retail 4.5 (114) plus Lyra 1.4.0 is device-verified.
- Spanish is repurposed; this is not a fourth independently selectable language.
- SET DATE, mods, installed, archived, system remain English for compatibility.
- TV-specific archives are not replaced; shared global strings can still affect
  TV wording, and TV output has not been device-tested.
- Not every page has been visually exercised.
- Lyra's observed repeated-initialization / disappearing-Mods behavior is not
  fixed by this localization. See RECOVERY.txt.
- 0.2.3 remains marked experimental. The public catalog's acceptance, generated
  table distribution, UI enable/disable path, and future-version guard behavior
  require maintainer review. No broad compatibility or acceptance is claimed.

This export contains no firmware image, original GEM, generated XUS, music,
device dump, raw device log, personal network identity, or compiled executable.
Generated packages are separate local outputs; source publication and catalog
binary distribution should be reviewed as separate decisions.

Upstream: https://github.com/project-lyra-zune/project-lyra
Docs: https://wiki.zune.moe/getting-started/mods-tab/
