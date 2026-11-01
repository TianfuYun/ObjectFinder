# ObjectFinder

ObjectFinder is an SSMS extension that helps you quickly find database objects (tables, views, procedures, functions, synonyms) from the currently selected database in Object Explorer.

## Features

- Fast in-memory search while you type
- Token-based matching (`schema name`, `schema.name`, fuzzy subsequence)
- One-key reload (`F5`) from current Object Explorer selection
- Jump to the selected object in Object Explorer
- Open generated `CREATE` / `ALTER` scripts in a new SQL document

## Requirements

- SQL Server Management Studio (SSMS), 64-bit
- Supported target range in manifest: `20.x` to `<24.0`

## Install

1. Build the solution in **Release**.
2. Find the generated `.vsix` package from the VSIX project output.
3. Close SSMS.
4. Install the `.vsix`.
5. Reopen SSMS.

## Usage

1. In SSMS Object Explorer, select a database node (or an object inside a database).
2. Open **Tools > Object Finder** (or use the configured shortcut).
3. Type to search.
4. Press:
   - `Enter` to select/reveal the highlighted object in Object Explorer
   - `F5` to reload object metadata
   - `Esc` to close the window
5. Use **CREATE**/**ALTER** links next to a result to open a generated script.

## Search Tips

- `dbo.sp` filters by schema and name prefix.
- `sales.` lists objects in `sales` schema.
- Multiple tokens require all tokens to match.
- Matching is case-insensitive.

## Compatibility Notes

ObjectFinder uses reflection to interact with SSMS Object Explorer internals. This keeps behavior resilient across SSMS updates, but future SSMS changes can still affect object navigation or scripting APIs. If something breaks after updating SSMS, please open an issue with the SSMS version and error message.

## Build and Test

```powershell
dotnet build ObjectFinder.sln -c Release
dotnet test ObjectFinderCore/tests/ObjectFinder.Core.Tests/ObjectFinder.Core.Tests.csproj -c Release
```

## License

No license file is currently included. Add one before publishing if you want clear reuse terms.
