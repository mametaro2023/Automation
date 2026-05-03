# Distribution

## Local Release Build

Create a Windows x64 zip containing a self-contained executable:

```powershell
.\scripts\publish.ps1 -Version 0.1.0
```

The output is written to:

```text
dist\AutomationTool-0.1.0-win-x64.zip
```

## GitHub Release

1. Create a GitHub repository.
2. Add it as this repository's `origin` remote.
3. Push the repository.
4. Push a version tag.

```powershell
git remote add origin https://github.com/<owner>/<repo>.git
git push -u origin master
git tag v0.1.0
git push origin v0.1.0
```

Pushing a `v*` tag runs `.github/workflows/release.yml`, builds the zip, and creates a GitHub Release.

## Notes

- The app is Windows-only.
- The release artifact is self-contained and does not require a separate .NET runtime install.
- No license has been selected yet. Add a license before making the repository public if redistribution terms matter.
