# Z-B brief: comment markers in Revit (Round Y item 4, Revit round-trip part 2)

Agreed 2026-10-22 (Gavin's answers; the family he authored to the Z-A spec: adaptive, one point, three status spheres
sized by *Radius*, type "Default"; no flag: BimGo_IsHighPriority stays as a parameter for filters).

- **Which comments:** all of them; closed ones keep their (green) marker. An Options tick removes closed ones.
- **When:** live sessions sync automatically (each comment change; one undo entry per sync). Without a live session,
  the ribbon's **Comment markers** button reads the model's BimGo comments (or a .bimgo file's).
- **Deleted in Revit:** stays gone until "put back deleted markers" (RE-PLACE ALL).
- **Family:** shipped with the add-in, loaded when the model doesn't have it; Options can point to another .rfa.
  Found by name, type "Default" (else the first type).
- One-way (BimGo → Revit); markers in the host model; parameters found by GUID.
