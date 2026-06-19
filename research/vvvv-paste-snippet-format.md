# vvvv paste-snippet (clipboard) format

Captured 2026-06-19 by copying a single **Box** node (Stride.Models) in gamma 7.2 and
reading the clipboard. This is the string `IDevSession.Paste(modelSnippet, location)` /
`SessionNodes.Paste(...)` expects — the basis for the agent adding nodes to a live patch.

## Shape

- Root element is **`<Canvas … CanvasType="Group">`** (a fragment, NOT a full `<Document>`).
- Declares `xmlns:p="property"` and `xmlns:r="reflection"`.
- Has an `Id` (the source canvas's id) and a `MergeId`. On paste, vvvv inserts into the
  **active** canvas and remaps ids, so the wrapper `Id` is a hint, not a destination.
- Children: the copied `<Node>`/`<Pad>`/`<Link>` elements, each with full
  `<p:NodeReference>` (Choice Kind/Name + `LastCategoryFullName` + `LastDependency`) and
  their `<Pin>`s.
- **Package deps ride along**: a `<NugetDependency>` appears *inside* the Canvas (here
  `VL.Stride`), so a pasted snippet carries the packages its nodes need.

## Captured Box snippet (verbatim)

```xml
<?xml version="1.0" encoding="utf-16"?>
<Canvas xmlns:p="property" xmlns:r="reflection" Id="Pl5JnrDCiHHPu1X7VLeQA5" MergeId="4" CanvasType="Group">
  <Node Bounds="609,101,165,19" Id="K2MmODjYdVoP2n4OPp49m6">
    <p:NodeReference LastCategoryFullName="Stride.Models" LastDependency="VL.Stride.vl">
      <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
      <Choice Kind="ProcessAppFlag" Name="Box" />
    </p:NodeReference>
    <Pin Id="MpNJnuwihJnMNIgE7ZFgT7" Name="Node Context" Kind="InputPin" IsHidden="true" />
    <Pin Id="NkGQ6rf7rOGLVM2ZVuzGAD" Name="Transformation" Kind="InputPin" />
    <Pin Id="RP4fkQSDI4XL92EeztJnZ3" Name="Size" Kind="InputPin" />
    <Pin Id="U7ObtK9QggzPqcpv9wfvEv" Name="Tessellation" Kind="InputPin" />
    <Pin Id="RB4rIrYAJGTL8AAq3sKiJw" Name="Material" Kind="InputPin" />
    <Pin Id="Oox8OikBezAOjEOg8tPs7L" Name="Is Shadow Caster" Kind="InputPin" />
    <Pin Id="PlXsCUE3buqNi4Wmy5Nx0P" Name="Components" Kind="InputPin" />
    <Pin Id="BDP6GCgzBfWNF3hsVeXFeJ" Name="Children" Kind="InputPin" />
    <Pin Id="V6bgAU5UgqkMAvJrcnBYcA" Name="Name" Kind="InputPin" />
    <Pin Id="DCoK34YJb1NOAFT5oG0AbJ" Name="Enabled" Kind="InputPin" />
    <Pin Id="M1wJfwZ0YTXOeRm5Hl15Lu" Name="Output" Kind="OutputPin" IsHidden="true" />
    <Pin Id="LAuBrJvPhRJOU7HYAeOO9c" Name="Entity" Kind="OutputPin" />
  </Node>
  <NugetDependency Id="RFsUDxKGXw5PCOBYgPjtsW" Location="VL.Stride" Version="2025.7.2" />
</Canvas>
```

## Implications for agent-driven node creation

- Build a **self-contained** Canvas snippet (all-new ids + internal `<Link>`s) so there are
  no cross-references to existing patch elements — paste it as one unit.
- `<Link Ids="sourcePinId,sinkPinId" />` wires pins; keep source-first.
- Generate fresh 22-char base62 ids for every element; vvvv remaps on paste but unique ids
  avoid ambiguity.
- Include the `<NugetDependency>` for any package the nodes need (e.g. `VL.Stride`).
- Pins not set/linked can be omitted on paste (vvvv fills from the definition) — but copying
  preserves them; safest to keep the pins you link to.
