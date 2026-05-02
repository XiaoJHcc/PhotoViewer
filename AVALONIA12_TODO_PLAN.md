# Avalonia 12 Migration Plan

## Goal

Bring PhotoViewer to a clean Avalonia 12 latest-style baseline, not just "builds", but with:

- no known Avalonia 11-era API leftovers that affect behavior
- compiled binding coverage cleaned up where practical
- Avalonia 12 input, focus, window decoration, and platform lifecycle semantics aligned
- a smaller set of reflection-binding escape hatches, each justified by data shape

## Phase 1: Clear high-priority migration debt

- [x] Fix current Avalonia binding compiler errors
- [x] Remove old event/API signatures that still rely on pre-12 behavior
- [x] Replace converter-produced unknown template item types with strongly typed view-model properties
- [x] Add missing `x:DataType` declarations to high-traffic templates

## Phase 2: Align with Avalonia 12 behavior changes

- [ ] Audit focus and keyboard-avoidance flows on settings/mobile views
- [ ] Audit selection and virtualization behavior in thumbnail list
- [ ] Audit TopLevel and dispatcher usage where code should prefer local dispatcher or presentation source semantics
- [ ] Audit custom window chrome against Avalonia 12 decorations model

## Phase 3: Move to the best available Avalonia 12 solution

- [x] Reduce `MultiBinding` and reflection-binding usage by moving UI composition into view models
- [ ] Rework custom title bar implementation toward Avalonia 12 decoration primitives where beneficial
- [ ] Revisit layout/binding converters in hot paths and replace with typed state when it simplifies XAML
- [ ] Run targeted validation for desktop and mobile startup-critical surfaces

## Execution Notes

- Start with items that currently fail binding compilation or block compiled bindings in core views.
- Prefer structural fixes over localized suppressions.
- Only keep reflection bindings where the data shape is intentionally dynamic.

## Progress Notes

- Completed the first migration pass for binding compilation blockers.
- Replaced the control-button nested symbol template with a dedicated reusable control.
- Updated key high-traffic templates to explicit `x:DataType` declarations.
- Moved EXIF value formatting and thumbnail placeholder visibility into view-model/model properties, removing remaining `MultiBinding` usage.
- Switched settings serialization to source-generated `System.Text.Json` metadata and cleared macOS trimming warnings.
- Reworked settings-page keyboard avoidance to use Avalonia 12 `InputPane` occlusion geometry instead of a hard-coded mobile keyboard height.
- Removed the custom touch-focus deferral state machine from `DeferredNumericTextBox` and aligned settings-page focus handling with Avalonia 12 native touch focus timing.
- Dropped the custom iOS numeric-keyboard workaround path and reverted iOS numeric settings inputs to Avalonia's native full-keyboard flow with built-in return-key handling.
- Validated with `dotnet build PhotoViewer/PhotoViewer.csproj -c Debug` and the macOS `Debug Mac` task.
- Revalidated after the keyboard-avoidance refactor with `dotnet build PhotoViewer/PhotoViewer.csproj -c Debug` and `dotnet build PhotoViewer.Mac/PhotoViewer.Mac.csproj -c Debug`.
- Revalidated the input-focus cleanup with `dotnet build PhotoViewer/PhotoViewer.csproj -c Debug` and `dotnet build PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Release -r ios-arm64`.
- Revalidated the simplified native iOS input flow with `dotnet build PhotoViewer/PhotoViewer.csproj -c Debug` and `dotnet build PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Release -r ios-arm64`.
- Next focus: audit Avalonia 12 focus semantics and evaluate replacing custom window chrome logic with decoration primitives.