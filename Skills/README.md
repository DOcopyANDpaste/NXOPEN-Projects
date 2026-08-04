# NXOpen C# patterns — moved

The `nxopen-csharp-patterns` skill (architecture layering + coding conventions for NXOpen C# tools) is
now maintained once, centrally, for reuse across every NX Open project:

```
..\NxOpen.Foundation\Skills\nxopen-csharp-patterns\
    SKILL.md
    references\common.md
    references\with-block-ui.md
    references\without-block-ui.md
```

This repo intentionally keeps only this pointer, not a working copy — per the chosen update model
(manual copy-forward, not a directory junction), the docs here do **not** auto-propagate. If this
repo needs its own live copy for skill discovery, copy the four files above into
`.claude\skills\nxopen-csharp-patterns\` by hand, and repeat that copy whenever the canonical version
changes.
