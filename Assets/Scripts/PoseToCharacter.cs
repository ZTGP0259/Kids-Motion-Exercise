using UnityEngine;

/// <summary>
/// Maps MediaPipe arm landmarks onto a Humanoid character using swing-twist
/// decomposition in world space.
///
/// Previous approaches failed because:
///   - LookRotation: aligns wrong bone axis (Z instead of bone's actual axis)
///   - FromToRotation: only 2 DOF, missing twist → wrong elbow bend
///   - Parent-local basis: becomes stale when Animator changes parent bones
///
/// This approach:
///   1. SWING: FromToRotation(bindAim, currentAim) rotates bone to point correctly
///   2. TWIST: Aligns bend plane around aim axis → correct elbow direction
///   3. Works in WORLD SPACE (bone.rotation) → immune to parent changes
/// </summary>
[RequireComponent(typeof(Animator))]
public class PoseToCharacter : MonoBehaviour
{
    [Header("Pose Source")]
    public PoseDetectionManager poseManager;

    [Header("Front Camera")]
    [Tooltip("Negate X for mirror effect. Enable for front camera.")]
    public bool mirrorX = true;

    [Header("Smoothing")]
    [Range(1f, 30f)]
    public float smoothSpeed = 14f;

    [Header("Visibility")]
    [Range(0.1f, 0.9f)]
    public float visibilityThreshold = 0.5f;

    private Animator _animator;
    private int _logCounter;

    private struct ArmState
    {
        public Transform Upper;
        public Transform Lower;

        // T-pose world rotations (captured once at Start)
        public Quaternion UpperBindRot;
        public Quaternion LowerBindRot;

        // T-pose aim directions in world space (constant)
        public Vector3 UpperBindAim;
        public Vector3 LowerBindAim;
        public Vector3 BindBendNormal;

        // Smoothed output (world rotations)
        public Quaternion UpperSmooth;
        public Quaternion LowerSmooth;

        // Bend normal tracking (root-local for frame consistency)
        public Vector3 LastBendLocal;
    }

    private ArmState _left, _right;

    private void Awake() => _animator = GetComponent<Animator>();

    private void Start()
    {
        if (_animator.avatar == null || !_animator.avatar.isHuman)
        {
            Debug.LogError("[PoseToCharacter] Humanoid avatar required.");
            enabled = false;
            return;
        }

        _left = BuildArm(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        _right = BuildArm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);

        if (_left.Upper == null || _right.Upper == null)
        {
            Debug.LogError("[PoseToCharacter] Missing arm bones.");
            enabled = false;
            return;
        }

        Debug.Log($"[PoseToCharacter] Ready. L.bindAim={_left.UpperBindAim:F2} R.bindAim={_right.UpperBindAim:F2} " +
                  $"L.bend={_left.BindBendNormal:F2}");
    }

    private ArmState BuildArm(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand)
    {
        Transform u = _animator.GetBoneTransform(upper);
        Transform l = _animator.GetBoneTransform(lower);
        Transform h = _animator.GetBoneTransform(hand);
        if (u == null || l == null) return default;
        if (h == null) h = l.childCount > 0 ? l.GetChild(0) : l;

        Vector3 uAim = (l.position - u.position).normalized;
        Vector3 lAim = (h.position - l.position).normalized;

        // Bend plane: cross product of upper and lower arm directions
        Vector3 bend = Vector3.Cross(uAim, lAim);
        if (bend.sqrMagnitude < 0.0001f)
        {
            // Arm straight in T-pose — assume elbows bend forward (toward camera)
            bend = transform.forward;
        }
        bend.Normalize();

        return new ArmState
        {
            Upper = u,
            Lower = l,
            UpperBindRot = u.rotation,
            LowerBindRot = l.rotation,
            UpperBindAim = uAim,
            LowerBindAim = lAim,
            BindBendNormal = bend,
            UpperSmooth = u.rotation,
            LowerSmooth = l.rotation,
            LastBendLocal = transform.InverseTransformDirection(bend)
        };
    }

    private void LateUpdate()
    {
        if (poseManager == null) return;

        float blend = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);

        bool lVis = poseManager.LeftShoulderVisibility  >= visibilityThreshold &&
                    poseManager.LeftElbowVisibility      >= visibilityThreshold &&
                    poseManager.LeftWristVisibility       >= visibilityThreshold;

        bool rVis = poseManager.RightShoulderVisibility >= visibilityThreshold &&
                    poseManager.RightElbowVisibility     >= visibilityThreshold &&
                    poseManager.RightWristVisibility      >= visibilityThreshold;

        // ── Debug every ~1.5s ──
        _logCounter++;
        if (_logCounter % 90 == 0)
        {
            Vector3 ls = poseManager.LeftShoulder;
            Vector3 le = poseManager.LeftElbow;
            Vector3 rs = poseManager.RightShoulder;
            Vector3 trkDir = (Lm(le) - Lm(ls)).normalized;
            Vector3 worldDir = transform.TransformDirection(trkDir);
            Debug.Log($"[PTC] lVis={lVis}(sv{poseManager.LeftShoulderVisibility:F1} ev{poseManager.LeftElbowVisibility:F1} wv{poseManager.LeftWristVisibility:F1}) " +
                      $"rVis={rVis} | LS({ls.x:F2},{ls.y:F2}) LE({le.x:F2},{le.y:F2}) RS({rs.x:F2},{rs.y:F2}) " +
                      $"trkDir={trkDir:F2} worldDir={worldDir:F2} bindAim={_left.UpperBindAim:F2}");
        }

        if (lVis)
        {
            SolveArm(ref _left,
                Lm(poseManager.LeftShoulder), Lm(poseManager.LeftElbow), Lm(poseManager.LeftWrist),
                blend);
        }

        if (rVis)
        {
            SolveArm(ref _right,
                Lm(poseManager.RightShoulder), Lm(poseManager.RightElbow), Lm(poseManager.RightWrist),
                blend);
        }
    }

    private void SolveArm(ref ArmState arm, Vector3 shoulder, Vector3 elbow, Vector3 wrist, float blend)
    {
        Vector3 upperDir = elbow - shoulder;
        Vector3 lowerDir = wrist - elbow;
        if (!Normalize(ref upperDir) || !Normalize(ref lowerDir)) return;

        // ── Bend plane from arm triangle ──
        Vector3 bendLocal = Vector3.Cross(upperDir, lowerDir);
        if (bendLocal.sqrMagnitude < 0.001f)
        {
            bendLocal = arm.LastBendLocal;
        }
        else
        {
            bendLocal.Normalize();
            if (Vector3.Dot(bendLocal, arm.LastBendLocal) < 0f)
                bendLocal = -bendLocal;
        }
        arm.LastBendLocal = bendLocal;

        // ── Convert to world space ──
        Vector3 upperWorld = transform.TransformDirection(upperDir);
        Vector3 lowerWorld = transform.TransformDirection(lowerDir);
        Vector3 bendWorld  = transform.TransformDirection(bendLocal);

        // ── Solve upper arm ──
        if (arm.Upper != null)
        {
            Quaternion target = SwingTwist(arm.UpperBindAim, upperWorld, arm.BindBendNormal, bendWorld, arm.UpperBindRot);
            arm.UpperSmooth = Quaternion.Slerp(arm.UpperSmooth, target, blend);
            arm.Upper.rotation = arm.UpperSmooth;
        }

        // ── Solve lower arm ──
        if (arm.Lower != null)
        {
            Quaternion target = SwingTwist(arm.LowerBindAim, lowerWorld, arm.BindBendNormal, bendWorld, arm.LowerBindRot);
            arm.LowerSmooth = Quaternion.Slerp(arm.LowerSmooth, target, blend);
            arm.Lower.rotation = arm.LowerSmooth;
        }
    }

    /// <summary>
    /// Computes a target world rotation via swing-twist decomposition.
    ///
    /// SWING: Rotates the bind aim direction to the current aim direction.
    ///        This is the "pointing" part — where does the bone aim?
    ///
    /// TWIST: After swing, the bend plane normal may not match the detected one.
    ///        We twist around the aim axis to align it.
    ///        This constrains the elbow to bend in the correct anatomical direction.
    ///
    /// Result: twist * swing * bindRotation
    /// </summary>
    private static Quaternion SwingTwist(
        Vector3 bindAim, Vector3 currentAim,
        Vector3 bindBend, Vector3 currentBend,
        Quaternion bindRot)
    {
        // Step 1: Swing — rotate from bind aim to current aim
        Quaternion swing = Quaternion.FromToRotation(bindAim, currentAim);

        // Step 2: Twist — align bend normal around the aim axis
        // The swing moved the bind bend normal; see where it ended up:
        Vector3 swungBend = swing * bindBend;

        // Project both onto the plane perpendicular to current aim
        // (only the component perpendicular to the aim axis matters for twist)
        Vector3 swungProj  = Vector3.ProjectOnPlane(swungBend, currentAim);
        Vector3 targetProj = Vector3.ProjectOnPlane(currentBend, currentAim);

        Quaternion twist = Quaternion.identity;
        if (swungProj.sqrMagnitude > 0.0001f && targetProj.sqrMagnitude > 0.0001f)
        {
            twist = Quaternion.FromToRotation(swungProj.normalized, targetProj.normalized);
        }

        // Step 3: Combine — twist * swing * bindRotation
        return twist * swing * bindRot;
    }

    /// <summary>
    /// Converts a MediaPipe landmark to root-local 2D position.
    /// Z depth is ignored (too noisy on mobile).
    /// </summary>
    private Vector3 Lm(Vector3 landmark)
    {
        float x = landmark.x - 0.5f;
        if (mirrorX) x = -x;
        float y = 0.5f - landmark.y;
        return new Vector3(x, y, 0f);
    }

    private static bool Normalize(ref Vector3 v)
    {
        float m = v.magnitude;
        if (m < 0.0001f) return false;
        v /= m;
        return true;
    }
}
