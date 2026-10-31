# Acoustic software used by engineers, and where BimGo could fit (research note)

This note follows `0_plan.md`. It is desk research from 2026-10-31, based on web search results. Vendor sites (datakustik.com,
odeon.dk, autodesk marketplace, aecmag.com) could not be opened from the research session because of its network
policy, so some details come from search summaries and secondary listings. Prices are rarely published. The figures
quoted are third-party listings and **unverified**. Check the vendor pages before relying on any row below.

## The landscape (by job, not by vendor)

| Job | Typical tools | Method | What they output | BIM link |
|---|---|---|---|---|
| **Room acoustics** (halls, classrooms, offices, plant rooms) | ODEON (editions: Basics, Industrial, Auditorium, Combined; v16), CATT-Acoustic + TUCT, AFMG EASE 5, Ramsete; free: Pachyderm (Rhino / Grasshopper), I-Simpa (GPLv3, with the SPPS particle tracer and a classical reverberation code) | Geometric acoustics: ray tracing + image sources, scattering; statistical formulas for checks | RT (T20 / T30 / EDT), SPL, C80, STI, grid maps over audience areas, auralization | Import of DWG / DXF / SketchUp / Rhino. EASE 5 takes Revit through DWG. Rooms usually need manual simplification and closing (EASE warns that unclosed models give wrong results). |
| **Wave-based / hybrid, cloud** | Treble (Reykjavik; Series A-2 $18M) | Wave-based (FEM-type) for low and mid frequencies + geometric for high frequencies, run in the cloud | Low-frequency modes, diffraction, interference; heat maps, auralization | A free Revit add-in that exports rooms, simplifies geometry and sends them to the cloud app. Reported pricing is $299–499 / month or "from €149" (listings disagree). |
| **Industrial / workplace noise indoors** | DataKustik **CadnaR**; ODEON Industrial | Ray / particle methods; the workroom prediction methods of ISO/TR 11690-3 | **The closest match to the pump-room case:** point, line, area and box sources with Lw and directivity; SPL at workplaces; **noise dose (dosimeter)**; absorbers on whole or partial walls; RT, STI; DIN 18041 checks | CAD import. Sold as a one-time purchase or subscription; cloud licences need maintenance. |
| **HVAC noise path (ASHRAE-style)** | Trane TAP, Daikin Acoustic Analyzer, Dynasonics AIM (free), AcousticCalc (last update 2016), Arup Strutt sheets, in-house spreadsheets | Source Lw → duct / plenum / breakout attenuation → statistical room effect (direct + reverberant) | NC / RC / dB(A) at the receiver, path by path | No geometry: these are tables. This is what most MEP engineers actually use day to day. |
| **Sound insulation between rooms** | INSUL 10 (Marshall Day), Bastian, SONarchitect, Acoulatis | EN 12354 / mass law / multi-layer models | TL / R per 1/3 octave, Rw / STC, Ln,w / IIC, rain noise | Construction databases; INSUL uses a dongle. |
| **Environmental noise mapping** (rooftop plant to the site boundary) | CadnaA, SoundPLAN, Predictor-LimA; open source: NoiseModelling | ISO 9613-2, CNOSSOS-EU | Grid noise maps at a height, facade levels | GIS / CAD import; quote-based pricing. |
| **Revit-native acoustics** | Acouso-ROOM; Treble's add-in; academic Sabine / Eyring plugins | Statistical RT + a ray view (Acouso) | RT per band, room schedules, standards checks, reflection paths | Inside Revit (Acouso: Revit 2023–2026). |
| **Game audio engines** (not engineering tools) | Steam Audio (Valve; the full SDK became open source under Apache 2.0 in Feb 2024), Microsoft Project Acoustics | Real-time propagation, baked or hybrid reverb, HRTF | Plausible real-time listening, not validated levels | Engine plugins. |

**What they share:**
- The slow, expensive step is **building the acoustic model**: import, simplify, close the room, assign absorption.
- The trusted tools are **validated** against measurement. The Chalmers comparison of ODEON vs CATT, and the 2025 Forum
  Acusticum classroom round robin, are examples.
- The value they protect most is **data**: material absorption libraries, construction TL databases, and equipment
  emission data.

## Where BimGo stands against each

| Capability | Benchmark | BimGo feasibility | Effort |
|---|---|---|---|
| Model with rooms, materials and element identity, no manual rebuild | Every GA tool needs import + cleanup. Treble's and Acouso's Revit add-ins are attacking this. | **Already have it:** Revit extraction, `RoomInfo`, materials, UniqueIds, a live session. This is BimGo's strongest advantage. | Exists |
| Sources placed on real equipment, Lw from family parameters, following edits | CadnaR / ODEON: sources are drawn by hand | **Better than the benchmarks:** a click on the pump, autofill from parameters, "all of this type", live updates | AC-B |
| SPL grid map, energy sum of sources, direct + reverberant | CadnaR, ODEON Industrial, TAP-style room effect | Statistical first, then hybrid ray tracing (`0_plan.md`). Can match for broadband dB(A) in plant rooms once validated. | AC-C / AC-D |
| Noise dose / 85 dB(A) zones, absorber what-ifs on parts of walls | CadnaR | Straightforward on top of the map (per-surface α overrides) | AC-C / AC-D |
| RT, EDT per band | All GA tools, Acouso | Sabine / Eyring at once. Decay-curve RT and EDT need **time-resolved** ray histograms, so the tracer should store energy by arrival time from day one. | AC-D |
| C80, D50, STI (with background noise) | ODEON, CATT, EASE, CadnaR | Feasible from the same impulse-response histograms (STI from the modulation transfer function, using IEC 60268-16). Matters for offices and teaching spaces, less for plant rooms. | Later |
| **Hearing it** (auralization) | ODEON, CATT, Treble (validated impulse responses) | **A distinctive option:** walk the model and hear the pumps at the calculated level. Use Steam Audio (Apache 2.0, compatible with MIT-0 distribution if its notice is shipped) for real-time propagation, calibrated to BimGo's levels. Present it as "indicative listening", not validated auralization. | Later, medium |
| Low-frequency modes, diffraction, interference | Treble's wave-based solver (its moat; a funded team, GPU / cloud) | **Not realistic to match.** State the Schroeder-frequency limit; perhaps an analytic room-mode hint for box-shaped rooms. | Out of scope |
| Transmission to neighbouring rooms (EN 12354, flanking) | INSUL, Bastian, SONarchitect | A simple path (TL / STC per wall type from parameters, Lp₂ = Lp₁ − TL + 10 log(S/A₂)) is feasible. Full EN 12354 flanking and a construction database are a product of their own. | Simple: AC-E. Full: no |
| HVAC duct path (fans → ducts → diffusers → room) | TAP, AIM, Daikin, spreadsheets | Feasible later: the ASHRAE tables are public, and the model knows the duct runs. A large separate feature, though, and the manufacturer tools are free. | Not now |
| Outdoor plant → boundary noise map | CadnaA, SoundPLAN, NoiseModelling | ISO 9613-2 on the site model is feasible in principle, but it is a different product area | Not now |
| Validation and credibility | Decades of round robins (ODEON / CATT) | **This must be earned:** a shoebox test vs Sabine / Eyring, free-field tests, one measured plant room, and a cross-check against I-Simpa / Pachyderm (free, so usable as references) | Ongoing |
| Absorption data | ODEON / CadnaR material libraries | Build an octave-band table with cited sources; manufacturer data and per-model overrides | AC-A / AC-D |

**Licensing constraints for reuse:**
- **I-Simpa** is GPLv3: use it as a validation reference only, not embedded in MIT-0 BimGo.
- **Pachyderm**'s licence was not confirmed: treat it the same way until checked.
- **Steam Audio** is Apache 2.0: it can be bundled, with its notice in `THIRD-PARTY-NOTICES.txt`.
- **`Lighting.Engine`**'s separate copyright is the precedent to avoid.

## Positioning (recommendation)

BimGo should not try to be ODEON, CATT or Treble. Concert halls, validated auralization and wave-based low-frequency
work are mature or well-funded niches.

The gap it can fill is the **MEP engineer's plant-room and equipment-noise screening**:
- **Today's choice:** the room-effect tables in TAP or a spreadsheet (no geometry, no map), or sending the model to an
  acoustic consultant who rebuilds it in CadnaR or ODEON.
- **What BimGo would offer:** sources on the real equipment, a map in minutes from the existing Revit model, dB(A) /
  NR and 85 dB(A) zones, what-ifs for absorbers and equipment selection, and listening while walking for clients.
- **How it stays credible:** export the source and absorption schedule (CSV / JSON), so a consultant can check it in
  their own tool.

**This research adds to `0_plan.md`:**
1. **AC-D:** record time-resolved energy (impulse-response histograms) in the tracer from the start. That keeps RT
   from the decay, EDT, C80 and STI open without a rewrite.
2. **Workplace outputs, like CadnaR's:** add the noise-dose output (exposure hours × level → LEX,8h) and partial-surface
   absorber what-ifs.
3. **Source shapes:** support line and box sources as well as points (CadnaR and ODEON offer them; large chillers need them).
4. **Validation corpus:** add I-Simpa and Pachyderm as free cross-check references.
5. **Optional listening build:** Steam Audio, labelled indicative.

## Sources

- Room acoustics comparisons:
  - Chalmers thesis, ODEON vs CATT: https://odr.chalmers.se/items/a9f008e4-be55-411a-bef6-bfdb0edacd3b
  - Forum Acusticum 2025 classroom comparison: https://dael.euracoustics.org/confs/fa2025/data/articles/000221.pdf
- Treble:
  - https://aecmag.com/simulation/treble-launches-sound-simulation-platform/
  - https://audioxpress.com/news/treble-raises-m-to-expand-its-acoustic-simulation-platform-for-physical-ai
  - Pricing (unverified): https://www.nomic.ai/glossary/treble-technologies, https://www.softwareadvice.com.au/software/420932/treble-acoustic-simulation-suite
- CadnaR:
  - https://www.datakustik.com/products/cadnar/cadnar/
  - https://www.datakustik.com/products/cadnar/cadnar-for-industry-acoustics
  - https://www.acoem.com/en/products/noise-vibration-softwares/cadnar/
- ODEON:
  - https://audioxpress.com/news/odeon-a-s-announces-version-16-of-the-odeon-room-acoustics-software
  - Power plant case (ODEON Industrial): https://jcaa.caa-aca.ca/index.php/jcaa/article/view/2416
- EASE 5: https://www.afmg.eu/en/ease, https://www.afmg.eu/en/ease-5-full-3d-model-import-autocad-rhino-and-sketchup
- CATT-Acoustic: https://www.catt.se/CATT-Acoustic.htm
- I-Simpa: https://i-simpa.univ-gustave-eiffel.fr/presentation, https://i-simpa-wiki.readthedocs.io/
- Pachyderm: https://www.food4rhino.com/en/app/pachyderm-acoustical-simulation
- HVAC:
  - Trane TAP: https://elibrary.tranetechnologies.com/public/trane-history-b/Literature/Sales/CDS-SLB007-EN_07172020.pdf
  - AcousticCalc: https://www.ibpsa.us/best-directory/acousticcalc-hvac-noise-prediction-program-2/
  - Dynasonics: https://www.esmagazine.com/articles/95474-noise-prediction--dynasonics
  - Arup Strutt: https://strutt.arup.com/help/Blank_Sheets/MechSheet.htm
  - Sound pressure → power conversion: https://labs.sonicfield.org/library/analysis-of-methods-for-converting-mechanical-equipment-manufacturers-sound-pres
- INSUL: https://www.insul.s05.system7.co.nz/, https://marshallday.com/innovation/software
- Revit-native:
  - Acouso: https://www.capterra.co.uk/software/1244687/Acouso
  - IOA event: https://pub.ioa.org.uk/events/integrating-acoustics-in-revit
  - Academic plugin: https://rsisinternational.org/journals/ijrias/article.php?id=8734
- Noise mapping licensing: https://www.capterra.com/p/248969/CadnaA/, https://soundofnumbers.zohodesk.eu/portal/en/kb/articles/cadnar-rental-conditions
- Steam Audio, Apache 2.0: https://phoronix.com/news/Steam-Audio-SDK-Fully-Open, https://fossforce.com/2024/02/valve-releases-steam-audio-as-open-source/
- ISO/TR 11690-3: https://www.iso.org/standard/19619.html
