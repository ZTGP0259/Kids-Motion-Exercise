# kme-002 — Full-Body Replication via Leg Tracking

## Metadata
- **ID**: kme-002
- **Type**: feature
- **Status**: specification
- **Complexity**: MEDIUM
- **Created**: 2026-04-17
- **Quality Gates**: all-approved

---

## Planning

### Description
Extend the existing pose-to-character pipeline to drive the character's legs (upper leg, lower leg) for both left and right, so the character replicates the player's full body motion — not just head + arms. Also fix the scene positioning so the full character is visible above ground, and ensure calibration verifies the player's hips are in frame before transitioning.

### Goal
When the player moves their legs (marches, kicks, bends a knee), the character's legs mirror the motion. The character is fully visible standing on the ground. Calibration guarantees the legs are detectable before gameplay begins.

### Objectives
- Extend `PoseToCharacter.cs` to solve `LeftUpperLeg`, `LeftLowerLeg`, `RightUpperLeg`, `RightLowerLeg` using the same swing-twist approach as arms
- Compute knee bend plane from hip–knee–ankle triangle (analogous to elbow bend plane)
- Add default knee-bend-forward fallback when leg is straight (anatomically correct)
- Update `CalibrationManager.cs` to also require both hips + ankles visible during T-pose hold (hip center point check + ankle check) — prevents player from calibrating without legs in frame
- Fix `GameScene.unity` character root Y position so the character stands on the ground plane (feet at floor, full body visible)
- Keep the existing jump-mirror, arm tracking, and head tracking untouched

### Deliverables
- `Assets/Scripts/PoseToCharacter.cs` — add `_leftLeg`, `_rightLeg` limb states built from hip/knee/ankle bones; extend LateUpdate to solve them
- `Assets/Scripts/CalibrationManager.cs` — add hip + ankle visibility to the 3-sec hold gate (silently, no new dots — uses existing instruction text)
- `Assets/Scenes/GameScene.unity` — character prefab Y position raised so feet rest on ground
- Test coverage for leg solving, knee bend direction, straight-leg fallback, and calibration gate expansion

---

## Specification

### Complexity Score: MEDIUM

### Complexity Rationale
- 3 files modified (`PoseToCharacter.cs`, `CalibrationManager.cs`, `GameScene.unity`)
- Reuses existing swing-twist infrastructure from arms — low novel-logic risk
- New landmark indices (`LEFT_KNEE=25`, `RIGHT_KNEE=26`) added to PoseDetectionManager
- Scene Y-position change is a single prefab override
- Calibration gate expansion adds 4 new visibility checks (2 hips, 2 ankles)
- No DB, no security surface, no architecture change

### Code Changes Required

| File | Action | Description |
|------|--------|-------------|
| `Assets/Scripts/PoseDetectionManager.cs` | modify | Add `LEFT_KNEE=25`, `RIGHT_KNEE=26` constants. Add public `LeftKnee`, `RightKnee` (Vector3) and `LeftKneeVisibility`, `RightKneeVisibility` (float). Populate in Update loop with rotation correction. Ankles are already exposed from kme-001. |
| `Assets/Scripts/PoseToCharacter.cs` | modify | Build `_leftLeg` and `_rightLeg` as `ArmState` instances (struct is generic enough for any upper/lower limb pair). Use `LeftUpperLeg`/`LeftLowerLeg`/`LeftFoot` bones from Humanoid avatar. LateUpdate drives them using the same `SolveArm` function. Pass `kLeftLegBendNormal = Vector3.right` (character's right = knee bends forward fallback) for left leg, and same for right leg (knees bend the same direction on both legs — unlike elbows). |
| `Assets/Scripts/CalibrationManager.cs` | modify | Expand the `headOk && leftOk && rightOk` gate: require `LeftHipVisibility`, `RightHipVisibility`, `LeftAnkleVisibility`, `RightAnkleVisibility` all ≥ `visibilityThreshold`. Update instruction text to "Stand straight, show both hands AND your legs to the camera". |
| `Assets/Scenes/GameScene.unity` | modify | Character prefab Y position: raise from 0 → 0.9 (approx half-height for this Mixamo character model so feet sit on ground). |

### Implementation Notes

**Reuse the `ArmState` struct for legs:**
The existing `ArmState` struct has no arm-specific logic — it's just "upper bone + lower bone + bind rotations + smoothing + last bend normal". I'll keep the struct name `ArmState` for now (it's internal; renaming would churn many lines). Add `_leftLeg` and `_rightLeg` fields alongside `_left` and `_right`.

**Bone mapping:**
```csharp
_leftLeg  = BuildArm(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot);
_rightLeg = BuildArm(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
```

**Bend plane convention for legs:**
Unlike arms (where left elbow bends forward via `Vector3.back` bend normal and right via `Vector3.forward` — mirror symmetry gives opposite cross products), **knees on both legs bend forward in the same rotational direction** because they are anatomically identical (both flex in the sagittal plane). The cross product of `hip→knee × knee→ankle` when the knee is bent forward will produce:
- Upper = (0, -1, 0) in tracking space (leg straight down)
- Lower = (0, -1, 0) (leg straight down)
- When knee bends forward: upper ≈ (0, -0.7, +0.7), lower = (0, -1, 0)
- `cross((0,-0.7,+0.7), (0,-1,0)) = (0.7, 0, 0)` → positive X

So for both legs: `kLegBendNormal = Vector3.right` (in tracking space, which transforms to world via `transform.TransformDirection`) as the fallback when legs are fully straight.

**Solver reuse:**
The existing `SolveArm` function takes upper/lower landmarks, bone transforms, bind basis + rotation, smoothed refs, bend normal, and a blend factor. It's agnostic to limb type. Call it twice more in LateUpdate.

**Visibility gate for legs in LateUpdate:**
```csharp
bool leftLegVis  = poseManager.LeftHipVisibility    >= visibilityThreshold &&
                   poseManager.LeftKneeVisibility   >= visibilityThreshold &&
                   poseManager.LeftAnkleVisibility  >= visibilityThreshold;
bool rightLegVis = poseManager.RightHipVisibility   >= visibilityThreshold &&
                   poseManager.RightKneeVisibility  >= visibilityThreshold &&
                   poseManager.RightAnkleVisibility >= visibilityThreshold;
```

**CalibrationManager gate expansion:**
```csharp
bool headOk  = poseManager.NoseVisibility          >= visibilityThreshold;
bool leftOk  = poseManager.LeftWristVisibility     >= visibilityThreshold;
bool rightOk = poseManager.RightWristVisibility    >= visibilityThreshold;
bool legsOk  = poseManager.LeftHipVisibility       >= visibilityThreshold &&
               poseManager.RightHipVisibility      >= visibilityThreshold &&
               poseManager.LeftAnkleVisibility     >= visibilityThreshold &&
               poseManager.RightAnkleVisibility    >= visibilityThreshold;
// gate = headOk && leftOk && rightOk && legsOk
```
No new UI dots — the existing 3 dots (head, L/R wrist) stay. Legs are silently validated. Instruction text updates to mention legs.

**Scene Y-fix:**
The character GameObject `Ch31_nonPBR` has `m_LocalPosition.y = -0`. Change to `0.9`. This is a prefab modification recorded in the scene YAML. Must be verified that the character's pivot is at waist/center (not feet); if pivot is at feet, Y=0 is correct and the problem is camera framing instead — this is verified visually during QA.

**FootBone handling:**
The BuildArm function uses the "hand" bone (end of chain) to derive the lower limb direction. For legs, we pass `LeftFoot`/`RightFoot`. If those are null on the Avatar, the fallback `lowerBone.childCount > 0 ? lowerBone.GetChild(0) : lowerBone` handles it.

---

## Test Cases

### Unit Tests
| # | Test Name | Input / Condition | Expected Result | Status |
|---|-----------|-------------------|-----------------|--------|
| 1 | test_knee_landmarks_exposed | `PoseDetectionManager` running with valid pose, knees visible | `LeftKnee`, `RightKnee` populated with rotation-corrected values; `LeftKneeVisibility`, `RightKneeVisibility` non-zero | pending |
| 2 | test_leg_bind_captured_on_start | `PoseToCharacter` Start runs with Humanoid avatar | `_leftLeg.UpperBindAim` and `_rightLeg.UpperBindAim` are non-zero world directions pointing approximately downward | pending |
| 3 | test_leg_solver_called_when_visible | All 3 left leg landmarks ≥ threshold | `SolveArm(ref _leftLeg, ...)` invoked; left upper leg bone rotation updated | pending |
| 4 | test_leg_solver_skipped_when_invisible | `LeftKneeVisibility < threshold` | Left leg bones rotation NOT updated; smoothing state unaffected for other limbs | pending |
| 5 | test_straight_leg_uses_default_bend_normal | Upper and lower leg directions collinear (cross product ≈ 0) | Falls back to `_leftLeg.LastBendLocal` (or default if first frame); no NaN rotations | pending |

### Functional Tests
| # | Test Name | Steps | Expected Result | Status |
|---|-----------|-------|-----------------|--------|
| 1 | test_marching_motion_mirrors | 1. Calibrate. 2. Lift one knee up (march). | Character's corresponding upper leg rotates upward; knee bends forward. | pending |
| 2 | test_both_legs_independent | 1. Calibrate. 2. Only move left leg. | Left leg moves on character; right leg stays still (T-pose/standing). | pending |
| 3 | test_character_fully_visible | 1. Enter GameScene. | Character's feet rest on ground plane; full body (head to feet) is in camera view. | pending |
| 4 | test_calibration_requires_legs | 1. Enter CalibrateScreen. 2. Show head + wrists but stand with legs out of frame. | Hold timer does NOT start; instruction says to show legs too. | pending |
| 5 | test_calibration_succeeds_full_body | 1. Enter CalibrateScreen. 2. Show head + wrists + full legs in frame. | Hold timer starts, counts down, scene transitions to GameScene. | pending |
| 6 | test_jump_still_works_after_leg_tracking | 1. Complete calibration. 2. Jump. | Character rises off ground (JumpDetector still works, no regression). | pending |

### Edge Cases
| # | Scenario | Expected Behaviour | Status |
|---|----------|--------------------|--------|
| 1 | Character avatar missing `LeftFoot`/`RightFoot` bones | Falls back to lower leg's first child; if none, uses lower leg itself (same as arms) | pending |
| 2 | Knee landmark index (25/26) returns valid data on MediaPipe | Exposed values match raw MediaPipe output (rotation-corrected) | pending |
| 3 | User crouches deeply (hips close to knees) | Legs correctly follow; no gimbal lock because `LastBendLocal` sign-consistency guard already present in `SolveArm` | pending |
| 4 | User sits on a chair (full 90° knee bend) | Legs bend forward properly; character in sitting posture (no chair mesh, just visually bent) | pending |
| 5 | One leg visible but not the other (e.g. user stepped sideways) | Only the visible leg updates; invisible leg holds last smoothed rotation | pending |
| 6 | Character prefab updated later with different Y-offset convention | Scene Y-fix is a prefab override; can be re-tuned without code change | pending |
| 7 | Player never moves legs (just stands) | Legs stay at bind rotation; micro-jitter from landmarks absorbed by existing smoothing | pending |
| 8 | Calibration gate never passes because legs can't be framed (e.g. small phone screen) | User can lower `CalibrationManager.visibilityThreshold` in Inspector or move further from camera; documented in QA notes | pending |

### Test Plan for QA Team

**Scope**: Verify the character replicates the player's full body motion including legs, and the character is fully visible in GameScene.

**Pre-conditions**:
- Debug build on Android device.
- Camera frames full body (phone propped up; user stands ~2m away).
- Character has Humanoid avatar with leg bones mapped.

**QA Steps**:
1. Launch app. CalibrateScreen shows. With only head + wrists visible (legs out of frame), confirm the hold timer does NOT start.
2. Step back so full body is visible. Dots turn green; hold timer counts down; scene transitions.
3. In GameScene, confirm the character is fully visible from head to feet, standing on the ground.
4. Stand still. Confirm character's legs are in rest (bind) pose, no spasms.
5. Lift left knee up to waist height (march step). Confirm character's left leg rotates similarly.
6. Lower left leg, lift right knee. Confirm only the right leg moves.
7. Do a squat (bend both knees). Confirm both legs bend.
8. Kick one leg forward. Confirm corresponding leg swings forward on character.
9. Jump. Confirm character rises (JumpDetector from kme-001 still works).
10. Sit on a chair (partial knee bend). Confirm character mimics the seated posture.

**Expected Outcomes**:
- Legs mirror natural kid motion (marching, squatting, kicking) at ≥ 70% visual accuracy.
- No regressions in arm tracking, head tracking, or jump mirror.
- Character full body visible in frame.
- Calibration refuses to complete without legs in frame.

**Out of Scope**:
- Foot/ankle rotation (only upper + lower leg bones driven).
- Foot placement on ground (no IK — feet may clip into ground if user crouches).
- Running gait analysis.

---

## Quality Gates

### Gate 1 — Senior Unity Developer Review
**Date**: 2026-04-17 | **Status**: Approved

| # | Severity | Finding | Location in Spec | Resolution |
|---|----------|---------|-----------------|------------|
| 1 | MEDIUM | Struct name `ArmState` is misleading when reused for legs | Implementation Notes | Documented decision; kept name internal-only for now. Future refactor opportunity logged but not blocking. |
| 2 | LOW | `BuildArm` third parameter is `HumanBodyBones hand` — semantically wrong for legs (we pass `LeftFoot`) | Code Changes | Method signature unchanged (the parameter is only used for computing the `lowerAim` direction, which works with any end bone); documented in Implementation Notes. |
| 3 | LOW | Scene prefab Y = 0.9 is a magic number specific to `Ch31_nonPBR` | Code Changes | Documented; if character is swapped later, Y must be re-tuned. Acceptable for current MVP. |
| 4 | MEDIUM | Calibration instruction text "Stand straight, show both hands AND your legs" — legs are implicit, not visualized with a dot | CalibrationManager change | Confirmed acceptable per user's request ("take the hip center point in calibration scene"). Implicit check via text + hold gate; no new UI element. |

**Verdict**: Approved — reuse of ArmState struct and SolveArm is a documented pragmatic choice. Bone mapping uses Humanoid Avatar so is rig-independent.

---

### Gate 2 — Mobile Performance & Runtime Safety
**Date**: 2026-04-17 | **Status**: Approved

| # | Severity | Category | Finding | Location in Spec | Mitigation |
|---|----------|----------|---------|-----------------|------------|
| 1 | LOW | Performance | Two additional `SolveArm` calls per LateUpdate (now 4 total: L arm, R arm, L leg, R leg) | Implementation Notes | Each call is O(1) — a handful of quaternion ops, no allocations. Negligible on modern mobile. |
| 2 | LOW | Safety | If any leg bone is null (e.g. non-humanoid avatar), `SolveArm` dereferences null bone transforms | Implementation Notes | Existing `SolveArm` has `if (arm.Upper != null)` guard; still works. `BuildArm` returns default `ArmState` if bones missing, so leg solvers become no-ops. |
| 3 | LOW | Safety | New `LEFT_KNEE=25`, `RIGHT_KNEE=26` indices — verify against MediaPipe pose spec | Code Changes | Confirmed: MediaPipe pose landmarker spec has LEFT_KNEE=25, RIGHT_KNEE=26. Unit Test #1 verifies exposure. |

**Verdict**: Approved — no HIGH/CRITICAL. Zero per-frame allocations. Runtime safety preserved by existing null guards.

---

### Gate 3 — Pre-Development Sweep
**Date**: 2026-04-17 | **Status**: Approved

**Part A — Gate 1 & 2 Resolution Confirmation**

| Finding | Status in Spec | Notes |
|---------|---------------|-------|
| G1-1: ArmState naming | Resolved — documented as pragmatic | — |
| G1-2: `hand` parameter semantics | Resolved — documented | — |
| G1-3: Scene Y magic number | Resolved — documented | — |
| G1-4: Silent leg validation | Resolved — per user request | — |
| G2-1: Extra solver calls | Resolved — negligible cost | — |
| G2-2: Null bone guards | Resolved — existing null checks | — |
| G2-3: Knee indices | Resolved — verified + test added | — |

**Part B — Predicted Implementation Bugs**

| # | Severity | Predicted Bug | Spec Location | Action Taken |
|---|----------|--------------|---------------|--------------|
| 1 | HIGH | `SolveArm` uses `arm.LastBendLocal` on the very first frame a limb is visible — if not initialized, bend plane is zero/undefined causing first-frame rotation pop | Existing code | `BuildArm` already sets `LastBendLocal = transform.InverseTransformDirection(bindBendWorld)` at construction — verified in current PoseToCharacter.cs. No fix needed. |
| 2 | MEDIUM | Knee bend default (Vector3.right in tracking space → transforms to world -X for 180° Y character) might flip for rigs where both legs mirror: need to test if both legs use same `kLegBendNormal` correctly | Implementation Notes | Added Edge Case #4 (sitting with 90° knee bend); QA will verify. If a leg bends wrong, it's a sign flip on the default — fast fix. |
| 3 | MEDIUM | Character Y=0.9 assumes pivot at waist. If `Ch31_nonPBR` actually has pivot at feet, character floats. | Code Changes | Test during QA (Functional Test #3). If wrong, single YAML value change. |
| 4 | LOW | User's hip Y that's captured as baseline for jump (kme-001) depends on whether user's full body is in frame. After kme-002 requires legs in calibration, the baseline framing will be more standardized — this actually improves kme-001 reliability | Side-effect | Positive side-effect, no action needed. |
| 5 | MEDIUM | Calibration legs gate may never pass on small phone screens (players can't frame full body at arm's reach) | Edge Case #8 | Documented; `visibilityThreshold` Inspector-tunable as workaround. Accept for MVP; revisit if reported. |
| 6 | LOW | If a user's clothing hides their ankles (e.g. long pants + dim lighting), MediaPipe visibility stays low → calibration stuck | Edge Case #8 | Documented; same workaround as above. |

**Verdict**: Approved — no new edge cases added (all predicted risks already covered). All Gate 1 & 2 HIGH/CRITICAL findings resolved. Spec is implementation-ready.

---

## Done
_Filled after all tests pass and PR is created._
