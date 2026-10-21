# Round X, build X-B2: brief (2026-10-19)

Baseline: X-B1 as built in this chat (not yet confirmed; one zip carries both).

From the X-B answers: "Swap Revit planting / entourage" (optional on load) and Gavin's own: "billboards should be
placeable but also able to swap all of a type or all of an instance, and be remembered to do so in case a user re-runs
bimgo down the line".

Decided without asking (Gavin away, "work through it"): every extraction records Planting / Entourage instances even
when their categories are unticked (cheap: no geometry); automatic swapping is on by default (by name, then size) with a
tick to turn it off; per type in ENTOURAGE → FROM REVIT (PICKED · REVIT · HIDE · AUTO); per instance with the Place gun
aimed at a swapped billboard (RMB swap, LMB hide, Z / X size, R mirror), CLEAR undoes the one-offs; rules keyed by
UniqueId (link instance / element in links) and "Family : Type", stored in the model folder's entourage.json (so Go, F5
and Export all keep them) or the .bimgo.
