# Sveta's Recipes

A Windows desktop app for a home pastry business: recipes with live costing (sub-recipes, labour, cost per serving),
scaling by amount, servings or pan size, ingredients and supplier prices, shopping lists, labels, invoices and
customers. It replaces a 2010-era MS Access database and was rebuilt with [Avalonia](https://avaloniaui.net) 12 and
.NET 10.

- `src/SvetaRecipes.Core`: domain model, SQLite (EF Core), costing / scaling / conversion engine
- `src/SvetaRecipes.Reports`: PDF output (recipes, labels, shopping lists, invoices) via QuestPDF
- `src/SvetaRecipes.App`: the Avalonia app
- `tools/`: legacy importer (Access `.mdb` → SQLite) and a headless screenshot / smoke-test harness
- `tests/`: engine tests and parity checks against the legacy data

No user data is kept in this repository. The legacy files and anything rendered from them live in git-ignored folders.

Releases are published here and installed apps update from them automatically
([Velopack](https://velopack.io)). See `CLAUDE.md` for architecture and `release.sh` for building.
