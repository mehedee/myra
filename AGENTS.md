# Myra project guidance

- Use `master` for macOS work and builds. The `desktop-windows-linux` branch maintains the .NET desktop port in `desktop/`.
- Keep Myra naming consistent in source, projects, namespaces, identifiers, scripts, documentation and release assets.
- Run builds and verification in the foreground. Do not submit detached background build jobs.
- Preserve personal library data, explicit download paths and credentials across updates. Profile migration is explicit and retains original data for rollback.
- AI and subtitle network requests remain user initiated. Do not run paid cloud inference without explicit authorization.
- Verify recommendation constraints, grounded title identities and runtime budgets with isolated fixtures. Report native platform coverage separately from host/headless tests.
