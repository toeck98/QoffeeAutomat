"""MkDocs hook (plugin ``gen-files``): builds the generated parts of the user documentation (task T604).

* The reference pages are produced by ``generate_reference.py`` from the single sources (metric catalogs,
  ``protocol/``, the OpenAPI snapshot), so they cannot drift and are never committed.
* The icon of the site is copied from ``assets/brand/`` (the single source of all icons, PLAN.md S05).
"""

import sys
from pathlib import Path

import mkdocs_gen_files

sys.path.insert(0, str(Path(__file__).resolve().parent))

import generate_reference

for relative_path, content in generate_reference.generate_all().items():
    with mkdocs_gen_files.open(relative_path, "w") as handle:
        handle.write(content)

icon = generate_reference.ROOT / "assets" / "brand" / "icon.png"
with mkdocs_gen_files.open("assets/icon.png", "wb") as handle:
    handle.write(icon.read_bytes())
