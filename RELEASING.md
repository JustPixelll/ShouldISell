# Releasing Should I Sell?

The repository is designed as a custom Dalamud repository. Publishing a GitHub Release triggers `.github/workflows/release.yml`, which builds the plugin, uploads `latest.zip`, and updates `pluginmaster.json`.

## First public release — v0.8.0

1. Confirm the project builds locally:

```powershell
dotnet build .\ShouldISell\ShouldISell.csproj -c Release
```

2. Push the complete repository to `main`.
3. Wait for the normal **Build Should I Sell** GitHub Action to pass.
4. On GitHub, open **Releases** → **Draft a new release**.
5. Choose **Create new tag** and enter:

```text
v0.8.0
```

6. Target branch: `main`.
7. Release title:

```text
Should I Sell? v0.8.0
```

8. Paste the contents of `RELEASE_NOTES_v0.8.0.md` into the release description.
9. Publish the release.
10. Wait for **Release Should I Sell** to finish.
11. Confirm the release contains `latest.zip`.
12. Confirm `pluginmaster.json` on `main` points to the published release.
13. Test the custom repository URL in Dalamud:

```text
https://raw.githubusercontent.com/JustPixelll/ShouldISell/main/pluginmaster.json
```

## Future releases

1. Update `<Version>` in `ShouldISell/ShouldISell.csproj`.
2. Update README/changelog as needed.
3. Commit and push to `main`.
4. Wait for CI to pass.
5. Publish a GitHub Release with a matching tag, e.g. `v0.9.0`.
6. The release workflow will build `latest.zip`, attach it to the release and update `pluginmaster.json` automatically.

The tag and project version should stay aligned: tag `v0.9.0` ↔ project version `0.9.0.0`.
