# kme-003 — Torso Tracking + Squat Y-Offset + Wrist Orientation

## Metadata
- **ID**: kme-003
- **Type**: feature
- **Status**: specification
- **Complexity**: MEDIUM
- **Created**: 2026-04-17
- **Quality Gates**: all-approved

---

## Planning

### Description
Three related enhancements that together make the character mirror the player's whole-body motion more faithfully:

1. **Chest/torso tracking** — rotate the character's Spine bone based on the player's shoulder–hip orientation, so leaning and twisting show up on the character.
2. **Squat Y-offset** — allow the JumpDetector to translate the character DOWN (not just up) so that when the player squats, the character's hips lower too and the feet stay grounded.
3. **Wrist orientation** — rotate the hand bones based on wrist→index-finger direction so hands point correctly (e.g. when the user holds palms forward vs sideways).

### Goal
Character's chest follows the player's torso lean/twist. Squatting brings the character down so it stops floating. Hands rotate to match hand direction.

### Objectives
- Drive `Spine` (or `Chest` if available) bone as a clamped swing rotation from the torso basis (right = right-shoulder − left-shoulder; up = mid-shoulder − mid-hip)
- Drive `LeftHand` / `RightHand` bones as a clamped swing from wrist→index direction
- Expose `LeftIndex` / `RightIndex` (MediaPipe landmarks 19/20) from `PoseDetectionManager`
- Modify `JumpDetector` to allow negative Y offset (squat mirror), with a clamp so the character root never dips below ground
- Keep existing head, arm, leg, and jump tracking untouched

### Deliverables
- `Assets/Scripts/PoseDetectionManager.cs` — `LeftIndex` (19), `RightIndex` (20) landmarks + visibility
- `Assets/Scripts/PoseToCharacter.cs` — `TorsoState` + `SolveTorso`; `SolveHand` for wrists; calls in LateUpdate
- `Assets/Scripts/JumpDetector.cs` — allow negative `_targetYOffset`, clamp floor at `-basePosition.y + minGroundClearance`
- Test coverage for torso swing, wrist rotation, and squat Y-offset

---

## Specification

### Complexity Score: MEDIUM

### Complexity Rationale
- 3 files modified, no new files
- Torso rotation is similar pattern to head (swing-only, clamped, world-space)
- Wrist rotation is small new code (same pattern as torso/head)
- JumpDetector change is a single math tweak with a safety clamp
- No rig-specific changes; Humanoid bones cover all of these

### Code Changes Required

| File | Action | Description |
|------|--------|-------------|
| `Assets/Scripts/PoseDetectionManager.cs` | modify | Add `LEFT_INDEX=19`, `RIGHT_INDEX=20` constants + `LeftIndex`/`RightIndex` (Vector3) + `LeftIndexVisibility`/`RightIndexVisibility` (float). Populate in Update with rotation correction. |
| `Assets/Scripts/PoseToCharacter.cs` | modify | Add `TorsoState` struct (Spine bone + bind rotation + bind basis); `BuildTorso` at Start; `SolveTorso` in LateUpdate (shoulder-right + torso-up → rotation basis → clamped swing from bind). Add `SolveHand` for L/R wrists using wrist→index direction. |
| `Assets/Scripts/JumpDetector.cs` | modify | Remove `max(0, ...)` clamp so `hipDelta` can be negative (squat); add floor clamp so `_basePosition.y + _currentYOffset ≥ groundMinY`. |

### Implementation Notes

**Torso solver (subtle, clamped):**
Previous attempts failed because the spine rotation fought the head and made the whole body tilt. The fix:
- Only rotate Spine (or Chest) bone; head is still driven independently as world rotation → head stays upright regardless of spine.
- Clamp rotation to ±`torsoMaxAngle` (default 30°). Prevents extreme rotations from noisy landmarks.
- Use swing-only (FromToRotation on the torso "up" vector). Don't include twist to avoid feedback loops.
- Use `rawLandmarks` (no mirror flip) for the torso math since we want the character's torso to tilt the same direction as the user (like the head).

```csharp
Vector3 torsoUp = midShoulder - midHip;   // user's torso up direction
Vector3 torsoUpWorld = transform.TransformDirection(torsoUp);
Quaternion swing = Quaternion.FromToRotation(_torso.BindUpWorld, torsoUpWorld);
// clamp to torsoMaxAngle
Quaternion target = swing * _torso.BindRot;
_torso.Smooth = Slerp(_torso.Smooth, target, blend);
_torso.Bone.rotation = _torso.Smooth;
```

**Hand (wrist) rotation:**
- Wrist bone direction in T-pose: wrist → middle finger (approx wrist → index works)
- MediaPipe gives landmark 19/20 for LeftIndex/RightIndex — approximately base of index finger
- Direction = Lm(index) - Lm(wrist), swing from bind, clamp to ±30°, smooth

**Squat Y-offset (JumpDetector):**
```csharp
// Allow downward when hipDelta is negative (squat).
// Remove the max(0, ...) clamp around hipDelta.
float hipDelta = BaselineHipY - _smoothedHipY;   // positive=up, negative=down (squat)
// Deadzone absorbs micro-jitter symmetrically
float adjusted = Mathf.Abs(hipDelta) <= deadzone
    ? 0f
    : hipDelta - Mathf.Sign(hipDelta) * deadzone;
_targetYOffset = adjusted * jumpHeightScale;
// Clamp so character root stays above ground (don't sink below basePosition.y - minFloor)
float minOffset = -_basePosition.y + minGroundClearance;
_targetYOffset = Mathf.Max(_targetYOffset, minOffset);
```
New Inspector field: `minGroundClearance = 0.05f` (5cm above world Y=0 minimum).

**Visibility gates:**
- Torso: requires both shoulders + both hips visible
- Hands: require wrist + index visible; if not, hand bone stays at bind rotation

---

## Test Cases

### Unit Tests
| # | Test Name | Input / Condition | Expected Result | Status |
|---|-----------|-------------------|-----------------|--------|
| 1 | test_index_landmarks_exposed | Valid pose | `LeftIndex`, `RightIndex` populated; visibility > 0 | pending |
| 2 | test_torso_bind_captured | Start with humanoid rig | `_torso.Bone` is non-null (Spine or Chest); `BindUpWorld` points approx (0, 1, 0) | pending |
| 3 | test_torso_rotation_clamped | Feed torsoUp 90° off bind | Resulting rotation ≤ `torsoMaxAngle` | pending |
| 4 | test_hand_rotation_skipped_when_index_invisible | `LeftIndexVisibility < threshold` | Left hand bone world rotation unchanged this frame | pending |
| 5 | test_squat_produces_negative_offset | `hipDelta = -0.1`, deadzone 0.015, scale 3.5 | `_targetYOffset < 0`; character translates down | pending |
| 6 | test_squat_clamped_above_ground | Very deep squat (hipDelta = -0.5) | `_currentYOffset ≥ -_basePosition.y + minGroundClearance` | pending |

### Functional Tests
| # | Test Name | Steps | Expected Result | Status |
|---|-----------|-------|-----------------|--------|
| 1 | test_torso_leans_with_user | 1. Calibrate. 2. Lean torso left / right / forward. | Character chest tilts in the same direction; head stays upright. | pending |
| 2 | test_hands_rotate | 1. Calibrate. 2. Point palm forward vs sideways. | Character hand bone orientation visibly changes. | pending |
| 3 | test_squat_feet_stay_grounded | 1. Calibrate. 2. Squat down. | Character's hips lower proportionally; feet stay near the ground plane (no visible floating). | pending |
| 4 | test_jump_still_works | 1. Calibrate. 2. Jump. | Character rises then returns — existing behavior unchanged. | pending |
| 5 | test_no_chest_tilt_affects_head | 1. Lean chest side to side. | Head stays upright (world rotation independent). | pending |

### Edge Cases
| # | Scenario | Expected Behaviour | Status |
|---|----------|--------------------|--------|
| 1 | Character avatar has `Spine` only, no `Chest`/`UpperChest` | Use `Spine` bone — already handled by the null-fallback chain | pending |
| 2 | Hip visibility drops mid-squat | Target offset resets toward 0 via existing output smoothing (no snap) | pending |
| 3 | User twists at waist (both shoulders rotate, hips static) | Torso rotates as much as `torsoMaxAngle` allows; looks natural | pending |
| 4 | Very small player in frame (all landmarks close together) | Torso direction still normalizes correctly; swing is small angle | pending |
| 5 | Index landmark noise produces 360° spins | Clamped to ±30°; prevents wild hand flicks | pending |

### Test Plan for QA Team

**Scope**: Verify chest tracking, hand rotation, and squat ground-contact.

**Pre-conditions**: Debug build, calibration completed, full body in frame.

**QA Steps**:
1. Stand still. Verify chest/torso is steady (no drift).
2. Lean torso forward as if bending to tie shoes. Character chest tilts forward; head stays upright.
3. Lean left and right. Character matches.
4. Twist at the waist (shoulders rotate, hips stay). Character chest twists.
5. Point one palm toward camera, then sideways. Character hand rotates.
6. Squat slowly. Character's hips lower; feet stay on ground.
7. Rise and jump. Character rises (jump still works).
8. Do a full sit (very deep squat). Character lowers but doesn't sink below the ground.

**Expected Outcomes**:
- Torso replicates lean/twist at ≥ 70% accuracy.
- Hand orientation matches when index finger is clearly visible.
- No floating during squats.
- No head tilting regressions from kme-001 / kme-002.

**Out of Scope**:
- Finger bending (MediaPipe pose doesn't track fingers individually — requires Hand Landmarker).
- Full foot IK (feet may still partially clip on very uneven poses).
- Character world-space walking (deferred — user confirmed "we can also check it at last").

---

## Quality Gates

### Gate 1 — Senior Unity Developer Review
**Date**: 2026-04-17 | **Status**: Approved

| # | Severity | Finding | Resolution |
|---|----------|---------|------------|
| 1 | HIGH | Previous torso attempts caused head tilt — regression risk | Head solver already sets world rotation, independent of spine. Spine rotation won't affect head visual. Added Functional Test #5 to verify. |
| 2 | MEDIUM | `Spine` bone might not exist on all humanoid rigs (e.g. simplified avatars) | Null-fallback: try `UpperChest` → `Chest` → `Spine`; first non-null wins |
| 3 | LOW | `minGroundClearance = 0.05` is a magic number | Inspector-tunable; Range 0–0.5 |

**Verdict**: Approved — the head-tilt risk is mitigated by world-rotation ordering.

### Gate 2 — Mobile Performance & Runtime Safety
**Date**: 2026-04-17 | **Status**: Approved

| # | Severity | Category | Finding | Mitigation |
|---|----------|----------|---------|------------|
| 1 | LOW | Perf | 3 additional per-frame bone writes (torso + 2 hands) | O(1) each, negligible |
| 2 | LOW | Safety | Null spine bone on non-humanoid rigs | Null guard: skip torso solving if `_torso.Bone == null` |
| 3 | LOW | Safety | Index landmarks (19/20) — verify per MediaPipe spec | Confirmed: MediaPipe pose landmarker spec lists LEFT_INDEX=19, RIGHT_INDEX=20 |

**Verdict**: Approved.

### Gate 3 — Pre-Development Sweep
**Date**: 2026-04-17 | **Status**: Approved

**Part A**: All G1/G2 findings resolved in spec text.

**Part B — Predicted bugs:**

| # | Severity | Predicted Bug | Action |
|---|----------|--------------|--------|
| 1 | MEDIUM | Torso rotation axis: using only "up" vector via FromToRotation doesn't capture twist (shoulder rotation around vertical axis). Twist detection needs BOTH "up" and "right" axes — would require full basis rotation | Acceptable for MVP: FromToRotation on "up" catches lean but not pure twist. Documented as limitation. Can be upgraded later using Quaternion.LookRotation(right, up) basis delta. |
| 2 | LOW | Squat floor clamp interacts with jump: if user jumps while offset is negative (was squatting), they might not reach full jump height | In practice, user lands before jumping again. Output smoothing handles transition. |
| 3 | MEDIUM | Hand rotation with only one point (index) is under-constrained — won't capture palm twist around wrist axis | Acceptable: hand swing is visible but not fully 3D-oriented. Future improvement using pinky + thumb for full hand basis. |
| 4 | LOW | `JumpDetector` renaming consideration — now handles squat too | Not renaming; comment updated to reflect "hip-Y mirror" role |

**Verdict**: Approved — limitations documented, acceptable for MVP.