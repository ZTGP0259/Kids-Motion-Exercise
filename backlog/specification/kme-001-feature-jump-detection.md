# kme-001 — Jump Detection Using MediaPipe Pose

## Metadata
- **ID**: kme-001
- **Type**: feature
- **Status**: specification
- **Complexity**: MEDIUM
- **Created**: 2026-04-17
- **Quality Gates**: all-approved (re-run after design pivot to direct Y translation)

---

## Planning

### Description
Extend the existing MediaPipe pose tracking (head + hands) to also detect when the player jumps. Use hip landmark Y-position relative to a calibrated baseline, combined with upward velocity, to trigger a "Jump" state on the character's Animator. Tracking should be forgiving for kids — small jumps must still register — and must not false-trigger on normal movement.

### Goal
When the player performs a jump in front of the camera, the character visibly reacts by playing a Jump animation. Detection is robust enough that small kid jumps are caught but normal standing/walking does not cause false positives.

### Objectives
- Expose `LeftHip`, `RightHip`, `LeftAnkle`, `RightAnkle` landmarks from `PoseDetectionManager` (hips already exposed; ankles need to be added)
- Capture `baselineHipY` during the CalibrateScreen phase (when player stands still in T-pose)
- Compute `jumpThreshold = baselineHipY + 0.1f` by default; support optional auto-calibration from a small practice jump
- Continuously compute average hip Y and its upward velocity
- Trigger jump when hipY crosses threshold AND upward velocity exceeds a small minimum
- Enforce a ~0.5s cooldown between jump triggers
- Apply smoothing to hip Y to reduce jitter
- Fire `Animator.SetTrigger("Jump")` on detection
- Keep detection forgiving for kids (small jumps should count)

### Deliverables
- `PoseDetectionManager.cs` — expose `LeftAnkle`, `RightAnkle` + their visibility properties
- `CalibrationManager.cs` — capture `baselineHipY` at T-pose hold, store in static field alongside existing T-pose data
- New script `JumpDetector.cs` — MonoBehaviour that reads landmarks, smooths hip Y, detects jumps, fires Animator trigger
- `GameScene.unity` — attach `JumpDetector` to character GameObject, wire reference to `PoseDetectionManager`
- Animator Controller update — add a `Jump` trigger parameter and a Jump state/transition
- Test cases covering baseline capture, threshold detection, velocity gate, cooldown, and small-kid-jump sensitivity

---

## Specification

> **REVISION NOTE (2026-04-17)**: User requested pivot from "detect jump → trigger Animator state (canned clip)" to "directly translate character Y in proportion to user's hip displacement" (pure mirror). This removes the Animator dependency entirely, simplifies the code, and gives 1:1 replication of the user's jump height. Gates 1–3 re-run below.

### Complexity Score: MEDIUM

### Complexity Rationale
- 3 files touched (PoseDetectionManager + ankles, CalibrationManager + baseline capture, new JumpDetector with root translation)
- Cross-cutting: landmark pipeline + static calibration storage + character transform manipulation in LateUpdate
- No DB/migration, no security surface
- No Animator Controller / parameter setup required anymore
- Moderate risk: character root position affects visibility and perceived camera framing

### ⚠️ Critical coordinate clarification
MediaPipe Y axis is inverted vs. common convention:
- `y = 0` → **top** of screen (player jumped UP)
- `y = 1` → **bottom** of screen (player standing/squatting)

Therefore when the user jumps UP, `hipY` **decreases** (gets smaller).

Displacement formula used for translation:
```
hipDelta = baselineHipY - smoothedHipY     // positive when user is UP, negative when crouched
worldYOffset = max(0, hipDelta - deadzone) * jumpHeightScale
character.position.y = _basePosition.y + worldYOffset
```

We only apply upward offset (clamp negative to 0). Crouches do not push the character below the ground.

### Code Changes Required

| File | Action | Description |
|------|--------|-------------|
| `Assets/Scripts/PoseDetectionManager.cs` | modify _(already done)_ | Add `LEFT_ANKLE=27, RIGHT_ANKLE=28` constants + ankle landmarks + visibility. |
| `Assets/Scripts/CalibrationManager.cs` | modify _(already done)_ | Capture `BaselineHipY` + `HasBaselineHipY` at T-pose hold. |
| `Assets/Scripts/JumpDetector.cs` | rewrite | Replace animator-trigger logic with **vertical translation**. Reads hip landmarks, applies exponential smoothing, computes `hipDelta`, translates character root Y = `basePosition.y + hipDelta * jumpHeightScale` (clamped ≥ 0). Smooth return to base when pose lost or user lands. |
| `Assets/Scenes/GameScene.unity` | modify | Attach `JumpDetector` component to character GameObject. Wire `poseManager` field. _(No Animator reference needed.)_ |
| ~~Character Animator Controller asset~~ | ~~modify~~ | **Not required anymore.** Kept optional for future animation polish. |

### Implementation Notes

**JumpDetector.cs signature (Option B — direct translation):**
```csharp
public class JumpDetector : MonoBehaviour
{
    public PoseDetectionManager poseManager;

    [Range(0.5f, 10f)]  public float jumpHeightScale = 3.5f;   // world units per unit hip displacement
    [Range(0f, 0.1f)]   public float deadzone        = 0.015f; // ignore sub-threshold jitter
    [Range(1f, 30f)]    public float hipSmoothSpeed  = 18f;    // input landmark smoothing
    [Range(1f, 30f)]    public float positionSmoothSpeed = 22f; // output Y ease
    [Range(0.1f, 0.9f)] public float visibilityThreshold = 0.4f;

    private Vector3 _basePosition;       // captured at Start — absolute ground position
    private float   _smoothedHipY;       // smoothed input landmark
    private float   _targetYOffset;      // computed offset for this frame
    private float   _currentYOffset;     // smoothed actual offset (applied to transform)
    private bool    _hasInitializedSmoothing;
    private bool    _disabled;
}
```

**Algorithm (runs in `LateUpdate`):**
1. At `Start()`: if `poseManager == null` OR `!CalibrationManager.HasBaselineHipY` → `_disabled = true`, log warning. Else capture `_basePosition = transform.position`.
2. Each `LateUpdate()`:
   - If `_disabled` → return.
   - **Visibility gate**: if either hip visibility < threshold → reset smoothing (`_hasInitializedSmoothing = false`), ease `_currentYOffset` toward 0 (so character returns to base when pose lost), apply, return.
   - Compute `rawHipY = 0.5 * (LeftHip.y + RightHip.y)`.
   - First valid frame → `_smoothedHipY = rawHipY`, `_hasInitializedSmoothing = true`, `_targetYOffset = 0`. Still apply ease-to-zero.
   - Subsequent frames:
     - Input smoothing: `blend = 1 - exp(-hipSmoothSpeed * dt)`, `_smoothedHipY = Lerp(_smoothedHipY, rawHipY, blend)`.
     - Compute delta: `hipDelta = BaselineHipY - _smoothedHipY` (positive when user jumped UP).
     - Deadzone: `hipDelta = max(0, hipDelta - deadzone)` (clamp negative + remove jitter).
     - World offset: `_targetYOffset = hipDelta * jumpHeightScale`.
   - Output smoothing: `outBlend = 1 - exp(-positionSmoothSpeed * dt)`, `_currentYOffset = Lerp(_currentYOffset, _targetYOffset, outBlend)`.
   - Apply: `transform.position = _basePosition + Vector3.up * _currentYOffset`.

**Why LateUpdate (not Update):** Runs after Animator. If the character later gets a root-motion clip, our translation still wins. Also matches PoseToCharacter which runs in LateUpdate.

**Why two-stage smoothing (input + output):**
- Input smoothing filters landmark jitter.
- Output smoothing prevents pops when the user jumps then visibility briefly drops — the character eases back smoothly instead of snapping.

**Graceful fallbacks:**
- No `poseManager` or no baseline → component disables itself at Start; no per-frame work.
- Hip visibility lost → `_currentYOffset` eases to 0 → character smoothly returns to ground.
- User moves far away from camera (visibility good, but framing changes) → `_smoothedHipY` drifts; deadzone clamps micro-drift; major drift is a known limitation documented in Edge Cases.

**Ankle handling (per user request):** `LeftAnkle`, `RightAnkle`, and their visibility properties are exposed on `PoseDetectionManager` but **not consumed** by the current JumpDetector. They are available for future extensions (crouching, stepping, feet-off-ground confirmation).

**No Animator work:** Option B doesn't use the Animator at all. `[RequireComponent(typeof(Animator))]` is NOT added — the character still has an Animator for other systems (PoseToCharacter arm tracking), but JumpDetector doesn't depend on it.

---

## Test Cases

### Unit Tests
| # | Test Name | Input / Condition | Expected Result | Status |
|---|-----------|-------------------|-----------------|--------|
| 1 | test_baseline_captured_on_tpose | Player holds T-pose for full hold duration; both hip visibilities ≥ threshold | `CalibrationManager.BaselineHipY` set to avg of `LeftHip.y` and `RightHip.y`; `HasBaselineHipY == true` | pending |
| 2 | test_baseline_not_captured_if_hips_invisible | Visibility of either hip < threshold during T-pose hold | `HasBaselineHipY == false` after calibration ends | pending |
| 3 | test_base_position_captured_at_start | `_basePosition` captured at Start equals character's initial world position | After Start, `_basePosition == transform.position` | pending |
| 4 | test_input_smoothing_stable_when_stationary | Feed constant `rawHipY = 0.6` for 60 frames | `_smoothedHipY` converges to 0.6 within 2 frames | pending |
| 5 | test_hip_delta_clamped_when_crouching | `smoothedHipY > baselineHipY` (crouch) | `hipDelta` clamps to 0; `_targetYOffset == 0` | pending |
| 6 | test_deadzone_suppresses_micro_jitter | `hipDelta = 0.01` (below deadzone of 0.015) | `_targetYOffset == 0` | pending |
| 7 | test_scale_factor_applied | `hipDelta = 0.1`, `deadzone = 0.015`, `jumpHeightScale = 3.5` | `_targetYOffset = (0.1 - 0.015) * 3.5 = 0.2975` | pending |
| 8 | test_ankle_landmarks_exposed | `PoseDetectionManager` running with valid pose | `LeftAnkle`, `RightAnkle` populated with rotation-corrected values; visibility present | pending |

### Functional Tests
| # | Test Name | Steps | Expected Result | Status |
|---|-----------|-------|-----------------|--------|
| 1 | test_character_translates_up_when_user_jumps | 1. Calibrate. 2. Jump up in front of camera. | Character root Y rises visibly then smoothly returns to base. | pending |
| 2 | test_character_height_scales_with_jump_height | 1. Small jump. 2. Larger jump. | Larger jump produces visibly larger character vertical movement. | pending |
| 3 | test_character_stays_grounded_when_stationary | 1. Calibrate. 2. Stand still for 10s. | Character Y stays at base; no drift, no jitter. | pending |
| 4 | test_character_does_not_sink_when_squatting | 1. Calibrate. 2. Crouch/squat slowly. | Character stays at base Y. Does NOT move down below the ground. | pending |
| 5 | test_returns_to_base_when_visibility_lost | 1. Calibrate. 2. Jump. 3. While in air, step out of frame. | Character smoothly returns to base Y (doesn't freeze in air). | pending |

### Edge Cases
| # | Scenario | Expected Behaviour | Status |
|---|----------|--------------------|--------|
| 1 | `HasBaselineHipY == false` (e.g. GameScene loaded directly in Editor) | JumpDetector logs warning at Start, self-disables, no per-frame work, character stays at base position. | pending |
| 2 | Pose visibility drops mid-jump | `_currentYOffset` eases to 0 via output smoothing. No snap. Smoothing state reset so next valid frame re-initializes cleanly. | pending |
| 3 | Frame rate drop (`Time.deltaTime` spike to 0.2s) | `1 - exp(-k * dt)` formula clamps blend factor to sensible range; no overshoot. | pending |
| 4 | Visibility exactly equal to `visibilityThreshold` | Treated as valid (uses `≥`, not `>`). | pending |
| 5 | User steps further from camera during play (hip Y drifts) | Deadzone absorbs minor drift; larger re-framing causes character to drift up — documented as known limitation, fix is to re-enter CalibrateScreen. | pending |
| 6 | PoseDetectionManager returns zero vectors on first frame before model loads | Visibility will be 0; gate blocks processing; no spurious movement. | pending |
| 7 | User jumps twice rapidly (both above baseline consecutively) | Both reflected in character's Y position — no cooldown in mirror mode; this is correct behavior (real-time mirror). | pending |
| 8 | Character was previously at a non-zero Y in scene (e.g. 0.5 above ground) | `_basePosition` captures that Y; translations offset FROM it; character never goes below base. | pending |

### Test Plan for QA Team
> This section is copied verbatim into the PR description.

**Scope**: Verify jump detection triggers the character Jump animation for realistic kid jumps while avoiding false positives.

**Pre-conditions**:
- Debug build deployed to Android device.
- User is framed waist-up in portrait orientation, well-lit.
- Character has Humanoid avatar; Animator controller has a `Jump` trigger parameter and a visible Jump state transition.

**QA Steps**:
1. Launch app. Complete CalibrateScreen by holding T-pose with both wrists and head visible.
2. Scene transitions to GameScene. Arm tracking should already be working.
3. Jump once (normal adult jump). Verify character plays Jump animation.
4. Wait ~1 second. Jump again. Verify Jump plays again.
5. Perform a small jump (barely lift feet, ~10cm). Verify Jump still triggers.
6. Stand completely still for 15 seconds. Verify Jump never triggers.
7. Squat down slowly and rise slowly. Verify Jump does NOT trigger.
8. Jump twice within 0.3s. Verify only the first one triggers the animation.
9. Move off-camera briefly (make hips invisible), then return and jump. Verify jump still detects correctly after visibility recovers.
10. Launch GameScene directly (skip CalibrateScreen in Editor). Verify log warning appears and no crashes occur.

**Expected Outcomes**:
- Small kid jumps consistently detected (≥ 80% hit rate for ~10cm jumps).
- Zero false triggers during 15s of stillness.
- Zero false triggers from squats.
- Cooldown prevents double-triggers.
- Graceful warning when calibration skipped.

**Out of Scope**:
- Landing animation (only jump trigger is in scope).
- Auto-calibration from practice jump (separate task kme-XXX if approved later).
- Ankle-based feet-off-ground confirmation (ankles exposed but not consumed).
- Visual feedback (sound, particles) on jump — to be added separately.

---

## Quality Gates

> **Re-run on 2026-04-17** after design pivot from Animator trigger → direct Y translation.
> Earlier gate results (animator-based approach) are preserved in git history of this file.

### Gate 1 — Senior Unity Developer Review (RE-RUN)
**Date**: 2026-04-17 | **Status**: Approved

| # | Severity | Finding | Location in Spec | Resolution |
|---|----------|---------|-----------------|------------|
| 1 | HIGH | Direct `transform.position` writes can fight with Animator root motion if a clip moves the root (future risk if animations added later) | Implementation Notes | Documented: run in `LateUpdate` (after Animator), capture `_basePosition` once at Start. If a future clip has root motion, `Apply Root Motion` should be disabled on the Animator. |
| 2 | MEDIUM | `_basePosition = transform.position` captured at Start — if parent moves or scene loads with character mid-animation, base is wrong | Algorithm step 1 | Start runs before first Update/Animator tick; position is the scene-serialized value. Acceptable. Documented. |
| 3 | LOW | Y-axis convention (MediaPipe image Y vs Unity world Y) easy to confuse | Coord clarification section | Explicit callout block at top of Specification retained. |
| 4 | LOW | `jumpHeightScale = 3.5` is a magic number — should be Inspector-tunable | Implementation Notes | Already exposed as `[Range(0.5f, 10f)]` Inspector slider. |

**Verdict**: Approved — no blocking issues. LateUpdate + base position capture correctly handles bone hierarchy interactions.

---

### Gate 2 — Mobile Performance & Runtime Safety (RE-RUN)
**Date**: 2026-04-17 | **Status**: Approved

| # | Severity | Category | Finding | Location in Spec | Mitigation |
|---|----------|----------|---------|-----------------|------------|
| 1 | LOW | Performance | `transform.position = X` each frame — writing to transform is cheap but not free | Algorithm | Acceptable; single write per frame; no matrix allocations. |
| 2 | LOW | Performance | Debug.Log in per-frame path would flood logs | Implementation Notes | No per-frame logs in final version; optional log gated behind `#if UNITY_EDITOR` in debug builds only (TBD during implementation). |
| 3 | LOW | Safety | If `poseManager` field is left unwired in Inspector and `HasBaselineHipY` is true (possible via static persistence), NPE risk | Algorithm step 1 | Null-check on `poseManager` in Start; disable if missing. |
| 4 | LOW | Safety | Character's parent transform (if any) may scale or rotate `_basePosition` meaning | Implementation Notes | Character root has no parent in current scene (sibling of Camera etc.). Documented assumption. |

**Verdict**: Approved — no HIGH/CRITICAL perf or safety issues. All state is stack-local or long-lived fields; zero per-frame allocations.

---

### Gate 3 — Pre-Development Sweep (RE-RUN)
**Date**: 2026-04-17 | **Status**: Approved

**Part A — Gate 1 & 2 Resolution Confirmation**

| Finding | Status in Spec | Notes |
|---------|---------------|-------|
| G1-1: Root motion conflict | Resolved | LateUpdate + base capture documented |
| G1-2: Base position captured at Start | Resolved | Assumptions documented |
| G1-3: Y-axis convention | Resolved | Callout retained |
| G1-4: Scale factor magic number | Resolved | Inspector slider |
| G2-1/2: Per-frame writes/logs | Resolved | Single assignment, no logs in hot path |
| G2-3: poseManager null guard | Resolved | Start-time guard |
| G2-4: Parent transform | Resolved | Assumption documented |

**Part B — Predicted Implementation Bugs**

| # | Severity | Predicted Bug | Spec Location | Action Taken |
|---|----------|--------------|---------------|--------------|
| 1 | MEDIUM | If `jumpHeightScale` is too small, jumps are invisible; too large, character leaves camera frame | Inspector tuning | Default 3.5; QA tunes on device. Edge Case implicit in QA step. |
| 2 | MEDIUM | Static `BaselineHipY` persists across scene reloads. If player re-launches GameScene without re-calibrating, stale baseline used | — | Already covered by existing Edge Case #1 (direct launch bypassing calibration). Acceptable for MVP. |
| 3 | LOW | Character's Y position written unconditionally — if another script also writes position (e.g. a controller), conflict | Implementation | No other script writes position currently. Documented assumption. |
| 4 | MEDIUM | User walks backward during play → hip Y decreases (further from camera = appears smaller on screen = hips move toward screen top). Character unintentionally drifts up | Edge Case #5 | Already documented as known limitation; deadzone absorbs minor movement; major re-framing requires re-calibration. |
| 5 | LOW | `_currentYOffset` eases toward 0 on visibility loss, but if visibility flickers mid-jump, could oscillate | Algorithm | Output smoothing (positionSmoothSpeed=22) dampens oscillation. Edge Case #2 covers visibility loss behavior. |

**Verdict**: Approved — no new edge cases or unit tests needed beyond the ones already added for the Option B design. Spec is implementation-ready.

---

## Done
_Filled after all tests pass and PR is created._