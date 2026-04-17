# Quality Gate Process — Kids Motion Exercise

> Adapted from the Zehntech SDD quality gate process for this Unity/C# mobile game project.
> See global `~/.claude/CLAUDE.md` for the canonical process definition.

---

## Philosophy

Every specification passes through three mandatory reviews before implementation begins. Each gate is a different critical lens on the same spec. The file stays in `backlog/planning/` until all three produce `Verdict: Approved`, then moves to `backlog/specification/`.

**Why three gates?**
- A bug found in the spec costs nothing to fix.
- A bug found in code costs hours.
- A bug found in production costs days.

---

## Gate 1 — Senior Unity Developer Review

**Role**: Senior Unity developer reading the spec for the first time.

**Checklist for this project:**

- [ ] `MonoBehaviour` lifecycle ordering is correct (Awake before Start, Start before Update, bone rotations in LateUpdate after Animator)
- [ ] All serialized `public` fields the spec adds are documented as needing Inspector assignment
- [ ] Humanoid bone mapping requirements stated (which `HumanBodyBones` are required)
- [ ] Scene references are consistent (GameObject names, file paths, GUIDs when relevant)
- [ ] Coroutines vs `async Task` choice is justified (prefer coroutines for Unity main-thread operations)
- [ ] `[RequireComponent]` attributes listed where relying on sibling components
- [ ] No direct `transform.rotation` writes without `LateUpdate` to override Animator
- [ ] MediaPipe landmark references use the separate `Visibility` floats, not `.z` (which is depth now)
- [ ] Any new scene asset is listed in `Build Settings` scene list
- [ ] For HIGH complexity: all files in Code Changes match Deliverables

---

## Gate 2 — Mobile Performance & Runtime Safety

**Role**: Third-party consultant auditing for mobile runtime issues.

**Performance checklist:**

- [ ] No `GameObject.Find`, `FindObjectOfType`, or `GetComponent` inside `Update`/`LateUpdate`
- [ ] No `new` allocations (GC pressure) inside per-frame paths — cache in fields
- [ ] No string concatenation in per-frame `Debug.Log` without a gate (e.g. `if (_logCounter % N == 0)`)
- [ ] `WebCamTexture.GetPixels32()` and `Texture2D.SetPixels32()` not called at resolutions higher than needed
- [ ] `MediaPipe.Image` properly disposed (`using` or explicit `Dispose`)
- [ ] No blocking `File.IO` on main thread — use `UnityWebRequest` for Android `StreamingAssets`
- [ ] Coroutines that poll use `yield return null` only when necessary; prefer `WaitUntil` / `WaitForSeconds`

**Runtime safety checklist:**

- [ ] Camera permission requested via `Application.RequestUserAuthorization(UserAuthorization.WebCam)`
- [ ] Null checks on `_animator.avatar`, bone transforms from `GetBoneTransform`, MediaPipe results
- [ ] Graceful shutdown: `WebCamTexture.Stop()` and `PoseLandmarker.Close()` in `OnDestroy`
- [ ] No sensitive data (device info, location, identifiers) logged to `Debug.Log`
- [ ] `Application.internetReachability` not assumed online

---

## Gate 3 — Pre-Development Sweep

**Role**: Lead developer about to open the editor.

**Part A**: Confirm every HIGH/CRITICAL finding from Gate 1 and Gate 2 is reflected in the current Specification text.

**Part B**: Predict Unity-specific implementation bugs and add missing edge cases:

- [ ] `NullReferenceException` on `Animator` properties accessed before the rig initializes
- [ ] Missing `[RequireComponent(typeof(...))]` causing silent failure when component is removed
- [ ] Scene load timing race — `Start()` coroutines running while another scene is still loading
- [ ] Inspector field left unassigned → runtime null ref (flag fields that MUST be wired)
- [ ] Bone hierarchy assumption (parent/child order) broken for non-Mixamo rigs
- [ ] Frame-rate-dependent smoothing (`Slerp(a, b, 0.1f)` vs `Slerp(a, b, 1 - Mathf.Exp(-speed * Time.deltaTime))`)
- [ ] Coroutine not stopped on scene change — orphaned MediaPipe inference
- [ ] Camera rotation angle (`videoRotationAngle`) assumed to be 0/90/180/270 — not handled for 0 default case
- [ ] Mirror direction wrong for rear camera users (if supported in future)
- [ ] Android back button / multitasking pause not handled (`OnApplicationPause`)

**Blocking rule**: Gate 3 is blocked if (a) any Gate 1/2 HIGH/CRITICAL finding isn't in the spec, OR (b) predicted bugs haven't been added as edge-case test rows.

---

## Recording gate results

Every gate result is recorded in the task file's `## Quality Gates` section (see global CLAUDE.md for template) AND appended as a changelog entry in `backlog/TODO.md`:

```
| 2026-04-17 15:30 | kme-001 | Gourav Patidar | Gate 1 approved — no component sync issues |
| 2026-04-17 15:45 | kme-001 | Gourav Patidar | Gate 2 approved — no HIGH/CRITICAL perf/safety issues |
| 2026-04-17 16:00 | kme-001 | Gourav Patidar | Gate 3 approved — 2 edge cases added; moved to specification/ |
```

If a gate was BLOCKED and resolved:

```
| 2026-04-17 15:30 | kme-001 | Gourav Patidar | Gate 2 blocked — 1 HIGH finding: GC allocation in LateUpdate; spec updated |
| 2026-04-17 15:45 | gourav-001 | Gourav Patidar | Gate 2 re-run — approved |
```
