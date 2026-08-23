# Releasing Should I?

Should I? is distributed as a custom Dalamud repository. Publishing a GitHub Release triggers `.github/workflows/release.yml`, which builds the plugin, uploads `latest.zip`, and updates `pluginmaster.json`.

> **Compatibility note:** the public product name is Should I?, while the technical project folder and Dalamud `InternalName` remain `ShouldISell` so existing installations update in place.

## Release checklist

1. Update `<Version>` in `ShouldISell/ShouldISell.csproj`.
2. Update public README/changelog/release notes as needed.
3. Build locally when practical:

```powershell
dotnet build .\ShouldISell\ShouldISell.csproj -c Release
```

4. Push/merge the release changes to `main`.
5. Wait for **Build Should I?** to pass.
6. Create a GitHub Release with a matching semantic tag, e.g. `v2.0.0`.
7. Use a release title such as `Should I? v2.0.0`.
8. Publish the release.
9. Wait for **Release Should I?** to finish.
10. Confirm the release contains `latest.zip`.
11. Confirm `pluginmaster.json` points install/update links at the new tag and advertises the matching assembly version.
12. Test the custom repository URL in Dalamud:

```text
https://raw.githubusercontent.com/JustPixelll/ShouldISell/main/pluginmaster.json
```

The tag and project version should stay aligned: `v2.0.0` ↔ `2.0.0.0`.
