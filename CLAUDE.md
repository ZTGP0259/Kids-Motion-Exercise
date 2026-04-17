# CLAUDE.md — Kids Motion Exercise

## Project Overview

Unity 6 Android game where kids exercise by mimicking on-screen character movements, captured via the phone's front camera using MediaPipe pose detection. Inspired by KinexPlay.

- **Engine**: Unity 6.3 LTS (6000.3.5f1)
- **Platform**: Android (IL2CPP, ARM64)
- **Orientation**: Portrait (locked)
- **Version**: 0.1.0 (pre-release)
- **Branch strategy**: `main` = stable, feature branches per task

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Android Device                       │
│  ┌──────────────┐     ┌─────────────────────────┐       │
│  │ Front Camera │ ──▶ │ MediaPipe PoseLandmarker│       │
│  └──────────────┘     └──────────┬──────────────┘       │
│                                  │  33 landmarks        │
│                                  ▼                      │
│                    ┌──────────────────────────┐         │
│                    │   PoseDetectionManager   │         │
│                    │   (rotation correction)  │         │
│                    └──────────┬───────────────┘         │
│                               │                         │
│            ┌──────────────────┼──────────────────┐      │
│            ▼                                     ▼      │
│  ┌──────────────────┐              ┌──────────────────┐ │
│  │CalibrationManager│              │ PoseToCharacter  │ │
│  │(CalibrateScreen) │              │  (GameScene)     │ │
│  │ T-pose detection │              │  Swing-twist IK  │ │
│  │ Save T-pose dirs │─────static──▶│  on Humanoid rig │ │
│  └──────────────────┘              └──────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

**Scene flow**: CalibrateScreen → (T-pose held 3s) → GameScene

---

## Key Files

| File | Purpose |
|------|---------|
| `Assets/Scripts/PoseDetectionManager.cs` | Front camera setup, MediaPipe inference, rotation-corrected landmarks, visibility scores |
| `Assets/Scripts/CalibrationManager.cs` | CalibrateScreen logic — 3 dot calibration, T-pose capture, scene transition |
| `Assets/Scripts/PoseToCharacter.cs` | Maps landmarks → Humanoid bones using swing-twist decomposition |
| `Assets/Scripts/StickFigureOverlay.cs` | Custom MaskableGraphic — draws stick figure on CalibrateScreen |
| `Assets/Scenes/CalibrateScreen.unity` | First scene — camera bg, stick figure, calibration dots |
| `Assets/Scenes/GameScene.unity` | Main gameplay — character with pose driving + camera preview |
| `Assets/StreamingAssets/pose_landmarker_lite.task` | MediaPipe pose model (loaded via UnityWebRequest for Android) |

---

## Pose Mapping Pipeline

1. **MediaPipe landmarks** — normalized (x, y) screen coords + relative z depth + visibility score
2. **PoseDetectionManager** — applies `videoRotationAngle` correction (raw camera is landscape on Android portrait)
3. **PoseToCharacter.Lm()** — converts to root-local 3D: `x ∈ [-0.5, 0.5]` (mirrored for front camera), `y` flipped (up = positive), `z = -mpZ * depthScale`
4. **SolveArm** — cross product of upper/lower dir = bend plane normal → `LookRotation(aim, bendNormal)` → delta from T-pose → applied to bind-pose world rotation
5. **SolveHead** — uses raw (non-mirrored) landmarks; swing-only rotation from mid-shoulders → nose direction; clamped to `headMaxAngle`

---

## Development Notes

**Unity / MediaPipe integration:**
- MediaPipe Unity Plugin v0.16.3 (`com.github.homuler.mediapipe`)
- `PoseLandmarker` in VIDEO running mode, `numPoses: 1`
- `.task` model must be in `StreamingAssets/` and loaded via `UnityWebRequest` (not `File.ReadAllBytes`) for Android `jar://` paths to work

**Humanoid rig rules:**
- Character must have Humanoid Avatar with bones mapped
- Character root rotated 180° Y (faces camera at -Z)
- Bone rotations set in `LateUpdate` (runs after Animator)
- Always set `bone.rotation` (world), never `localRotation`, so results are immune to parent Animator changes

**Coordinate conventions:**
- `mirrorX = true` for front camera (player's left visually on player's left on screen)
- Head tracking uses raw (non-mirrored) landmarks so tilt direction matches user
- `depthScale ≥ 0` controls 3D depth from MediaPipe z (0 = flat 2D, 0.5 = gentle forward/back)

**Testing:**
- ADB logcat filter: `[PoseToChar]`, `[PoseDetectionManager]`, `[CalibrationManager]`
- First-frame logs print bind-pose aim directions and rotation angle for debugging

---

## Project Management

- **Project Key**: `kme`
- **Task Index**: `backlog/TODO.md`
- **Security Rules**: `documents/security-rules.md`
- **Release Notes**: `RELEASE_NOTES.md`
- See global `~/.claude/CLAUDE.md` for SDD process and quality gate details.

**Task naming**: `kme-{001}-{feature|bug|task}-{short-description}`

**Note on adapting SDD to Unity/C#**: The global CLAUDE.md is written for Odoo. For this project the Gate checklists adapt as follows:
- Gate 1 (Senior Dev): MonoBehaviour lifecycle correctness (Awake/Start/Update/LateUpdate order), serialized field inspector wiring, Humanoid bone mapping, scene references consistency
- Gate 2 (Security/Performance): no allocations in per-frame paths, camera permission handling, Android StreamingAssets access pattern, no blocking `File.IO` on main thread
- Gate 3 (Pre-Dev Sweep): Unity-specific bug prediction (NullReferenceException on Animator before Start, missing `[RequireComponent]`, coroutines vs tasks, scene load timing races)
