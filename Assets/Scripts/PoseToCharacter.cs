using UnityEngine;

/// <summary>
/// Maps MediaPipe pose landmarks onto a Humanoid character using swing-twist
/// decomposition in world space.
///
/// Arms: swing-twist with bend plane from shoulder-elbow-wrist triangle.
/// Head: swing-only from nose position relative to mid-shoulders.
/// All rotations in world space (bone.rotation) — immune to Animator parent changes.
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

    [Header("Head")]
    [Range(0f, 40f)]
    [Tooltip("Max degrees the head can tilt from bind pose.")]
    public float headMaxAngle = 25f;

    [Range(1f, 20f)]
    public float headSmoothSpeed = 8f;

    [Header("Visibility")]
    [Range(0.1f, 0.9f)]
    public float visibilityThreshold = 0.5f;

    private Animator _animator;
    private int _logCounter;

    // ── Arm state ──
    private struct ArmState
    {
        public Transform Upper;
        public Transform Lower;
        public Quaternion UpperBindRot;
        public Quaternion LowerBindRot;
        public Vector3 UpperBindAim;
        public Vector3 LowerBindAim;
        public Vector3 BindBendNormal;
        public Quaternion UpperSmooth;
        public Quaternion LowerSmooth;
        public Vector3 LastBendLocal;
    }

    // ── Head state ──
    private struct HeadState
    {
        public Transform Bone;
        public Quaternion BindRot;      // world rotation in T-pose
        public Vector3 BindAimWorld;    // neck→head direction in T-pose
        public Quaternion Smooth;       // smoothed world rotation
    }

    private ArmState _left, _right;
    private HeadState _head;

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

        // ── Head setup ──
        Transform headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
        Transform neckBone = _animator.GetBoneTransform(HumanBodyBones.Neck);
        if (headBone != null && neckBone != null)
        {
            Vector3 headAim = (headBone.position - neckBone.position).normalized;
            _head = new HeadState
            {
                Bone = headBone,
                BindRot = headBone.rotation,
                BindAimWorld = headAim,
                Smooth = headBone.rotation
            };
            Debug.Log($"[PoseToCharacter] Head ready. bindAim={headAim:F2}");
        }

        Debug.Log($"[PoseToCharacter] Arms ready. L.bindAim={_left.UpperBindAim:F2} R.bindAim={_right.UpperBindAim:F2}");
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

        Vector3 bend = Vector3.Cross(uAim, lAim);
        if (bend.sqrMagnitude < 0.0001f)
            bend = transform.forward;
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

        float armBlend = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        float headBlend = 1f - Mathf.Exp(-headSmoothSpeed * Time.deltaTime);

        bool lVis = poseManager.LeftShoulderVisibility  >= visibilityThreshold &&
                    poseManager.LeftElbowVisibility      >= visibilityThreshold &&
                    poseManager.LeftWristVisibility       >= visibilityThreshold;

        bool rVis = poseManager.RightShoulderVisibility >= visibilityThreshold &&
                    poseManager.RightElbowVisibility     >= visibilityThreshold &&
                    poseManager.RightWristVisibility      >= visibilityThreshold;

        bool headVis = poseManager.NoseVisibility            >= visibilityThreshold &&
                       poseManager.LeftShoulderVisibility    >= visibilityThreshold &&
                       poseManager.RightShoulderVisibility   >= visibilityThreshold;

        // ── Debug every ~1.5s ──
        _logCounter++;
        if (_logCounter % 90 == 0)
        {
            Vector3 ls = poseManager.LeftShoulder;
            Vector3 le = poseManager.LeftElbow;
            Vector3 rs = poseManager.RightShoulder;
            Debug.Log($"[PTC] lVis={lVis} rVis={rVis} headVis={headVis} | " +
                      $"LS({ls.x:F2},{ls.y:F2}) LE({le.x:F2},{le.y:F2}) RS({rs.x:F2},{rs.y:F2})");
        }

        // ── Arms ──
        if (lVis)
        {
            SolveArm(ref _left,
                Lm(poseManager.LeftShoulder), Lm(poseManager.LeftElbow), Lm(poseManager.LeftWrist),
                armBlend);
        }

        if (rVis)
        {
            SolveArm(ref _right,
                Lm(poseManager.RightShoulder), Lm(poseManager.RightElbow), Lm(poseManager.RightWrist),
                armBlend);
        }

        // ── Head ──
        if (headVis && _head.Bone != null)
        {
            SolveHead(headBlend);
        }
    }

    // ═══════════════════════════════════════════
    //  ARM SOLVER
    // ═══════════════════════════════════════════

    private void SolveArm(ref ArmState arm, Vector3 shoulder, Vector3 elbow, Vector3 wrist, float blend)
    {
        Vector3 upperDir = elbow - shoulder;
        Vector3 lowerDir = wrist - elbow;
        if (!Norm(ref upperDir) || !Norm(ref lowerDir)) return;

        // Bend plane from arm triangle
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

        Vector3 upperWorld = transform.TransformDirection(upperDir);
        Vector3 lowerWorld = transform.TransformDirection(lowerDir);
        Vector3 bendWorld  = transform.TransformDirection(bendLocal);

        if (arm.Upper != null)
        {
            Quaternion target = SwingTwist(arm.UpperBindAim, upperWorld, arm.BindBendNormal, bendWorld, arm.UpperBindRot);
            arm.UpperSmooth = Quaternion.Slerp(arm.UpperSmooth, target, blend);
            arm.Upper.rotation = arm.UpperSmooth;
        }

        if (arm.Lower != null)
        {
            Quaternion target = SwingTwist(arm.LowerBindAim, lowerWorld, arm.BindBendNormal, bendWorld, arm.LowerBindRot);
            arm.LowerSmooth = Quaternion.Slerp(arm.LowerSmooth, target, blend);
            arm.Lower.rotation = arm.LowerSmooth;
        }
    }

    private static Quaternion SwingTwist(
        Vector3 bindAim, Vector3 currentAim,
        Vector3 bindBend, Vector3 currentBend,
        Quaternion bindRot)
    {
        Quaternion swing = Quaternion.FromToRotation(bindAim, currentAim);

        Vector3 swungBend = swing * bindBend;
        Vector3 swungProj  = Vector3.ProjectOnPlane(swungBend, currentAim);
        Vector3 targetProj = Vector3.ProjectOnPlane(currentBend, currentAim);

        Quaternion twist = Quaternion.identity;
        if (swungProj.sqrMagnitude > 0.0001f && targetProj.sqrMagnitude > 0.0001f)
        {
            twist = Quaternion.FromToRotation(swungProj.normalized, targetProj.normalized);
        }

        return twist * swing * bindRot;
    }

    // ═══════════════════════════════════════════
    //  HEAD SOLVER
    // ═══════════════════════════════════════════

    /// <summary>
    /// Tilts the head bone based on nose position relative to mid-shoulders.
    ///
    /// In T-pose, the direction from mid-shoulders to nose is straight up.
    /// When the user tilts their head, this direction changes.
    /// We apply a clamped swing rotation to match.
    /// </summary>
    private void SolveHead(float blend)
    {
        // Compute head direction: mid-shoulders → nose
        Vector3 midShoulder = 0.5f * (Lm(poseManager.LeftShoulder) + Lm(poseManager.RightShoulder));
        Vector3 nosePos = Lm(poseManager.Nose);
        Vector3 headDir = nosePos - midShoulder;
        if (!Norm(ref headDir)) return;

        // Convert to world space
        Vector3 headDirWorld = transform.TransformDirection(headDir);

        // Swing from bind direction to current
        Quaternion swing = Quaternion.FromToRotation(_head.BindAimWorld, headDirWorld);

        // Clamp rotation to prevent unnatural over-rotation
        float angle = Quaternion.Angle(Quaternion.identity, swing);
        if (angle > headMaxAngle)
        {
            swing = Quaternion.Slerp(Quaternion.identity, swing, headMaxAngle / angle);
        }

        Quaternion target = swing * _head.BindRot;
        _head.Smooth = Quaternion.Slerp(_head.Smooth, target, blend);
        _head.Bone.rotation = _head.Smooth;
    }

    // ═══════════════════════════════════════════
    //  UTILITIES
    // ═══════════════════════════════════════════

    /// <summary>
    /// Converts a MediaPipe landmark (now rotation-corrected by PoseDetectionManager)
    /// to root-local 2D position. Z depth ignored (too noisy on mobile).
    /// </summary>
    private Vector3 Lm(Vector3 landmark)
    {
        float x = landmark.x - 0.5f;
        if (mirrorX) x = -x;
        float y = 0.5f - landmark.y;
        return new Vector3(x, y, 0f);
    }

    private static bool Norm(ref Vector3 v)
    {
        float m = v.magnitude;
        if (m < 0.0001f) return false;
        v /= m;
        return true;
    }
}
