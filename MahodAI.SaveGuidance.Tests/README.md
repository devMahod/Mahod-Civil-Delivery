# Save continuation host-boundary tests

This separate test assembly links the complete production `CivilDeliveryControl.SaveGuidance.cs` and `CivilDeliveryControl.DrawingSaveContext.cs` and references the real Core readiness/token policies. Only Document events, command transport, Dispatcher and the other palette members are shims. The queue/state machine and save observer are not copied into test-only implementations. The actual `OnDrawingChanged` callback body remains covered by Plugin source contracts; this harness models that callback boundary for event/continuation composition.

Do not add these shims to Plugin.Tests: their deliberately substituted Autodesk/WPF/palette boundary types would collide with real host references. This project has no Plugin/WPF/Autodesk reference and ships no runtime payload.

Run `dotnet test MahodAI.SaveGuidance.Tests/MahodAI.SaveGuidance.Tests.csproj -p:AutoCADVersion=2027` (or `2026`). The root Directory.Build.props chooses the corresponding .NET target. Release validation must use isolated artifacts and the same no-bundle guard as the other focused lanes.

The 31 cases prove command ordering, event dispatch, cancellation, retry, same-path preservation, new-path notification and identity handling under deterministic host-boundary simulation. They do not prove native Civil command scheduling or real Save As dialogs; those remain separate native acceptance checks.
