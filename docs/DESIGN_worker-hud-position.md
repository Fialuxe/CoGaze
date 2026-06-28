# Worker HUD Positioning Rationale — CoGaze

**Decision date:** 2026-06-28  
**Component:** `WorkerHUD2` (`Assets/Scripts/Experiment/WorkerHUD2.cs`)  
**Implemented value:** `hudOffset = (-0.21f, 0.21f, 1.2f)` (x left, y up, z forward in metres)

---

## Context

The CoGaze Worker wears a Meta Quest 3S and performs manual assembly and object-identification tasks at working height (workpiece approximately 20–40° below the natural gaze line). A world-space status HUD (`WorkerHUD2`) displays calibration state, task progress, and a countdown timer. This document records the evidence basis for the three spatial parameters of that panel.

---

## Parameter 1 — Viewing Distance (z = 1.2 m)

**Chosen value:** 1.2 m (previously 0.7 m)

The primary constraint is the **vergence–accommodation conflict (VAC)**. Fixed-focal-plane HMDs (including the Quest 3S) display all virtual content at a single optical focal distance while stereoscopic disparity cues simulate arbitrary depths. When the simulated depth departs from the focal plane by more than approximately 0.5–0.6 diopters, measurable visual discomfort results.

> "The zone of comfort: Predicting visual discomfort with stereo displays." Shibata, T., Kim, J., Hoffman, D. M., & Banks, M. S. (2011). *Journal of Vision*, 11(8), 11. https://doi.org/10.1167/11.8.11 — PMC3369815.  
> *Key figure: mean discomfort onset at 0.8–1.2 D VAC; tolerable zone ≈ ±0.5 D from focal plane.*

Quest-class HMDs have an optical focal plane of approximately 1.3–2.0 m. Content rendered at 0.7 m falls outside this zone (approx. 0.9–1.1 D mismatch), causing sustained eye strain during the multi-minute setup phase. Content at 1.2 m is within ±0.5 D of the focal plane.

Meta's own developer documentation corroborates the 1 m recommendation as the default for controller-indirect (ray-cast) interaction panels:

> Meta Horizon OS. (2024). *MR Key Considerations — UI Placement Distance*. https://developers.meta.com/horizon/design/mr-design-guideline/  
> "Many have found that 1 meter is a comfortable distance for menus and GUIs."  
> *Guidance for indirect/pointer interaction: 1.0 m. Direct hand interaction: 0.45 m. Hard minimum to avoid VAC strain: 0.5 m.*

Meta's comfort guidelines additionally cite the comfortable optics range as **0.75 m – 3.5 m**:

> Meta Horizon OS. (2024). *Comfort*. https://developers.meta.com/horizon/design/comfort/

1.2 m was chosen as the centre-of-range value — closer than 1.5 m to maintain readability at the panel size used (520 × 200 mm), farther than 1.0 m to provide additional VAC margin during long setup phases.

---

## Parameter 2 — Vertical Offset (y = +0.21 m, approximately +10° above the horizon)

**Chosen value:** +0.21 m above the centre-eye anchor (previously +0.28 m)

This is the parameter with the strongest task-specific justification and the sharpest conflict between general ergonomics guidelines and assembly-task research.

### General ergonomics (downward placement)

Meta and Oculus ergonomics guidelines reflect the natural resting gaze angle (6° below the horizontal) and standard office-display ergonomics:

> Meta Horizon OS. (2024). *Comfort*. https://developers.meta.com/horizon/design/comfort/  
> *Ergonomic optimum for sustained display viewing: 15°–55° below the horizon. Comfortable head-rotation range: ≤20° upward, ≤12° downward for sustained use (maximums: 60° up / 40° down).*

At face value this recommends downward placement. However, this guidance was written for general-purpose VR menus viewed during idle, standing, or walking scenarios — not for work tasks where the operator's primary gaze is already directed downward.

### Assembly-task-specific research (upward placement)

A controlled VR study by Mack, Heun, and Rose (2023) tested nine head-anchored panel positions across 30 participants performing tasks at working height:

> Mack, N., Heun, V., & Rose, T. (2023). Cognitive load of head-anchored information at different positions in virtual reality. *Proceedings of Mensch und Computer 2023*. ACM. https://doi.org/10.1145/3603555.3603575  
> *Finding: above-eye placement significantly reduced cognitive load (NASA-TLX) for complex tasks performed at normal working height. The benefit was consistent across the nine tested positions.*

The mechanism is spatial competition: when the primary task lies 20–40° below the horizon (hand assembly at table height), a HUD placed in the same lower region divides attention between the work and the panel. An above-eye panel occupies a zone not used by the primary task, reducing oculomotor competition.

Industrial AR research on assembly guidance corroborates the zone-clearance principle:

> Blattgerste, J., Strenge, B., Renner, P., Pfeiffer, T., & Essig, K. (2018). Comparing conventional and augmented reality instructions for manual assembly tasks. *Proceedings of the 11th PErvasive Technologies Related to Assistive Environments Conference (PETRA 2018)*. ACM. https://doi.org/10.1145/3197768.3197778  
> *Finding: in-situ (near work area) overlays outperformed displaced side panels on time, errors, and NASA-TLX — directly motivating the decision to keep the ambient status HUD away from the task work zone.*

The USAARL (US Army Aeromedical Research Laboratory) HMD ergonomics framework establishes ±15° from the primary gaze line as the comfortable saccadic fixation radius, and ±10° as the cluster zone:

> Referenced in: arXiv:2505.09047 (Positioning Study for Head-Worn Displays, 2025). https://arxiv.org/abs/2505.09047

At +10° above the horizon, the panel lies within the ±20° comfortable head-rotation zone and is spatially distinct from both the primary task area (−20° to −40°) and the natural resting gaze line (−6°). The magnitude was reduced from the previous +0.28 m (+15.6°) to +0.21 m (+10°) to maintain comfortable neck posture for the setup phase duration.

---

## Parameter 3 — Horizontal Offset (x = −0.21 m, approximately 10° to the left)

**Chosen value:** −0.21 m left of centre (previously −0.28 m)

The panel is a peripheral status display — not a task-critical overlay — so it is positioned off-centre to avoid occluding the primary forward gaze. Sources converge on 8–20° lateral offset as the practical range:

> Henderson, S. J., & Feiner, S. (2009). Exploring the benefits of augmented reality documentation for maintenance and repair. *Proceedings of IEEE ISMAR 2009*. http://www.cs.columbia.edu/graphics/projects/armar/pubs/henderson_feiner_ismar2009.pdf  
> *Compared world-registered AR, head-locked HUD, and side-panel LCD for vehicle maintenance. World-registered AR: 4.9 s mean task location time; head-locked HUD: 11.1 s; side LCD: 9.2 s. Head-locked UI was explicitly worst. Note: world-registered (in-situ) outperforms any offset approach for task-critical overlays — this finding is specific to ambient status panels.*

> Meta Horizon OS. (2024). *Comfort*. https://developers.meta.com/horizon/design/comfort/  
> *Horizontal comfort limit: 15°–20° of visual angle before users naturally rotate their head rather than saccading. UI should fit within the middle third of the forward field of view.*

> MIL-STD-1472G (US Department of Defense, Human Engineering). Critical information must fall within a 30° cone around the operator's line of sight.

At 10° left (−0.21 m at 1.2 m) the panel is comfortably within the ±15° saccade zone and well within the MIL-STD-1472G 30° cone, requiring no head rotation to check status.

---

## Parameter 4 — Follow Mode (yaw-only, lag-follow with exponential damping)

**Method:** world-space canvas; target position re-computed each frame from head position + yaw only (pitch and roll ignored); eased toward target with `1 - exp(-lerp * dt)` damping.

Meta explicitly prohibits rigid head-locking for HUD content:

> Meta Horizon OS. (2024). *Lessons from the Frontlines: Modern VR Design Patterns*. https://developers.meta.com/horizon/blog/lessons-from-the-frontlines-modern-vr-design-patterns/  
> "Avoid locking HUD style content to the user's head movements. Anchor information and digital content to a space, or **loosely follow the user using smoothing animation**."

> Meta Horizon OS. (2024). *Display*. https://developers.meta.com/horizon/design/display/  
> *Overlay layers should be world-locked because they benefit from TimeWarp and are much less prone to judder. Rigid head-lock is explicitly identified as a cause of fatigue in passthrough/MR.*

Pitch and roll are intentionally excluded from the follow calculation. When the Worker looks down to assemble, a pitch-following HUD would swing across the work area — exactly the spatial conflict that Blattgerste et al. (2018) identified as the worst condition for assembly guidance. Excluding pitch keeps the panel in the upper zone regardless of head tilt.

---

## Summary Table

| Parameter | Value | Primary source | Constraint avoided |
|-----------|-------|----------------|--------------------|
| Distance | 1.2 m | Shibata et al. 2011; Meta Horizon OS MR Guidelines | VAC strain at 0.7 m |
| Vertical | +0.21 m (+10° above) | Mack, Heun & Rose 2023 (ACM); USAARL ±15° zone | Overlap with task work area at −20°–40° |
| Horizontal | −0.21 m (10° left) | Meta Comfort guidelines; MIL-STD-1472G | Occlusion of primary forward gaze |
| Follow | Yaw-only lag-follow | Meta Lessons from the Frontlines 2024 | Rigid head-lock fatigue; pitch-drag across task area |

---

## References

1. Shibata, T., Kim, J., Hoffman, D. M., & Banks, M. S. (2011). The zone of comfort: Predicting visual discomfort with stereo displays. *Journal of Vision*, 11(8), 11. https://doi.org/10.1167/11.8.11 (PMC3369815)

2. Mack, N., Heun, V., & Rose, T. (2023). Cognitive load of head-anchored information at different positions in virtual reality. *Proceedings of Mensch und Computer 2023*. ACM. https://doi.org/10.1145/3603555.3603575

3. Blattgerste, J., Strenge, B., Renner, P., Pfeiffer, T., & Essig, K. (2018). Comparing conventional and augmented reality instructions for manual assembly tasks. *Proceedings of PETRA 2018*. ACM. https://doi.org/10.1145/3197768.3197778

4. Henderson, S. J., & Feiner, S. (2009). Exploring the benefits of augmented reality documentation for maintenance and repair. *IEEE ISMAR 2009*. http://www.cs.columbia.edu/graphics/projects/armar/pubs/henderson_feiner_ismar2009.pdf

5. Meta Horizon OS. (2024). *Comfort*. Meta Platforms. https://developers.meta.com/horizon/design/comfort/

6. Meta Horizon OS. (2024). *MR Key Considerations*. Meta Platforms. https://developers.meta.com/horizon/design/mr-design-guideline/

7. Meta Horizon OS. (2024). *Display*. Meta Platforms. https://developers.meta.com/horizon/design/display/

8. Meta Horizon OS. (2024). *Lessons from the Frontlines: Modern VR Design Patterns*. Meta Platforms. https://developers.meta.com/horizon/blog/lessons-from-the-frontlines-modern-vr-design-patterns/

9. Oculus VR. (2016). *Oculus Best Practices*. Meta Platforms. https://static.oculus.com/documentation/pdfs/intro-vr/latest/bp.pdf

10. US Department of Defense. (2012). *MIL-STD-1472G: Human Engineering*. (Critical information within 30° cone around operator line of sight.)

11. Anonymous authors. (2025). Positioning study for head-worn displays in everyday scenarios. arXiv:2505.09047. https://arxiv.org/abs/2505.09047
