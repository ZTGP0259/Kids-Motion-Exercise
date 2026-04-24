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

    [Header("Depth (Z)")]
    [Range(0f, 3f)]
    [Tooltip("Scale for MediaPipe z depth. 0 = flat 2D, higher = more 3D depth. Start with 0.5.")]
    public float depthScale = 0.5f;

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
    private ArmState _leftLeg, _rightLeg;
    private FootState _leftFoot, _rightFoot;
    private FootState _leftHand, _rightHand;  // reuse FootState struct — same single-swing pattern
    private HeadState _head;
    private TorsoState _torso;

    // ── Torso state ──
    // Rotates Spine/Chest bone based on the user's torso "up" direction
    // (mid-shoulders − mid-hips). Head remains independent (world rotation).
    private struct TorsoState
    {
        public Transform Bone;
        public Quaternion BindRot;
        public Vector3 BindUpWorld;   // torso up in T-pose (world)
        public Quaternion Smooth;
    }

    [Header("Torso")]
    [Range(0f, 45f)]
    [Tooltip("Max degrees the spine/chest can tilt from bind pose.")]
    public float torsoMaxAngle = 30f;
    [Range(1f, 20f)]
    public float torsoSmoothSpeed = 10f;

    [Header("Hand (wrist)")]
    [Range(0f, 45f)]
    [Tooltip("Max degrees the hand bone can rotate from bind pose.")]
    public float handMaxAngle = 30f;

    // ── Foot state ──
    // Foot rotation is swing-only from ankle→footIndex direction.
    // In 2D this is limited but helps shoes not look stuck at a fixed angle.
    private struct FootState
    {
        public Transform Bone;
        public Quaternion BindRot;
        public Vector3 BindAimWorld;  // ankle→foot direction in T-pose (world)
        public Quaternion Smooth;
    }

    [Header("Foot")]
    [Range(0f, 30f)]
    [Tooltip("Max degrees the foot bone can rotate from bind pose.")]
    public float footMaxAngle = 20f;

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

        // Legs — reuse the ArmState struct; it's limb-agnostic.
        // Third bone is the "end" (hand for arms, foot for legs) used only to compute lower limb direction.
        _leftLeg  = BuildArm(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot);
        _rightLeg = BuildArm(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);

        // Foot bones — rotated by ankle→footIndex swing (swing-only, no bend plane).
        _leftFoot  = BuildFoot(HumanBodyBones.LeftFoot,  HumanBodyBones.LeftToes);
        _rightFoot = BuildFoot(HumanBodyBones.RightFoot, HumanBodyBones.RightToes);

        // Hand bones — rotated by wrist→index swing; reuse FootState (same single-swing pattern).
        _leftHand  = BuildSingleSwingBone(HumanBodyBones.LeftHand,  HumanBodyBones.LeftMiddleProximal);
        _rightHand = BuildSingleSwingBone(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal);

        // Torso — rotate Spine/Chest with player's torso "up" direction.
        _torso = BuildTorso();

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

        bool llVis = poseManager.LeftHipVisibility   >= visibilityThreshold &&
                     poseManager.LeftKneeVisibility  >= visibilityThreshold &&
                     poseManager.LeftAnkleVisibility >= visibilityThreshold;

        bool rlVis = poseManager.RightHipVisibility   >= visibilityThreshold &&
                     poseManager.RightKneeVisibility  >= visibilityThreshold &&
                     poseManager.RightAnkleVisibility >= visibilityThreshold;

        bool torsoVis = poseManager.LeftShoulderVisibility  >= visibilityThreshold &&
                        poseManager.RightShoulderVisibility >= visibilityThreshold &&
                        poseManager.LeftHipVisibility        >= visibilityThreshold &&
                        poseManager.RightHipVisibility       >= visibilityThreshold;

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
                poseManager.LeftShoulder, poseManager.LeftElbow, poseManager.LeftWrist,
                armBlend);
        }

        if (rVis)
        {
            SolveArm(ref _right,
                poseManager.RightShoulder, poseManager.RightElbow, poseManager.RightWrist,
                armBlend);
        }

        // ── Legs (hip → knee → ankle) + foot ──
        if (llVis)
        {
            SolveLeg(ref _leftLeg,
                poseManager.LeftHip, poseManager.LeftKnee, poseManager.LeftAnkle,
                poseManager.LeftFootIndex, poseManager.LeftFootIndexVisibility,
                armBlend);
        }

        if (rlVis)
        {
            SolveLeg(ref _rightLeg,
                poseManager.RightHip, poseManager.RightKnee, poseManager.RightAnkle,
                poseManager.RightFootIndex, poseManager.RightFootIndexVisibility,
                armBlend);
        }

        // ── Torso (Spine/Chest rotation) ──
        if (torsoVis)
        {
            float torsoBlend = 1f - Mathf.Exp(-torsoSmoothSpeed * Time.deltaTime);
            SolveTorso(torsoBlend);
        }

        // ── Hands (wrist→index swing) ──
        if (poseManager.LeftWristVisibility >= visibilityThreshold &&
            poseManager.LeftIndexVisibility >= visibilityThreshold)
        {
            SolveHand(ref _leftHand, poseManager.LeftWrist, poseManager.LeftIndex, armBlend);
        }
        if (poseManager.RightWristVisibility >= visibilityThreshold &&
            poseManager.RightIndexVisibility >= visibilityThreshold)
        {
            SolveHand(ref _rightHand, poseManager.RightWrist, poseManager.RightIndex, armBlend);
        }

        // ── Head ──
        if (headVis && _head.Bone != null)
        {
            SolveHead(headBlend);
        }
    }

    // ═══════════════════════════════════════════
    //  LIMB SOLVERS
    // ═══════════════════════════════════════════

    /// <summary>Arm solver — thin wrapper over SolveLimb with MediaPipe shoulder/elbow/wrist landmarks.</summary>
    private void SolveArm(ref ArmState arm, Vector3 shoulderLm, Vector3 elbowLm, Vector3 wristLm, float blend)
    {
        SolveLimb(ref arm, Lm(shoulderLm), Lm(elbowLm), Lm(wristLm), blend);
    }

    /// <summary>
    /// Leg solver — thin wrapper over SolveLimb with hip/knee/ankle landmarks,
    /// plus optional foot bone rotation from ankle→footIndex direction.
    /// </summary>
    private void SolveLeg(ref ArmState leg,
        Vector3 hipLm, Vector3 kneeLm, Vector3 ankleLm,
        Vector3 footIndexLm, float footIndexVisibility,
        float blend)
    {
        SolveLimb(ref leg, Lm(hipLm), Lm(kneeLm), Lm(ankleLm), blend);

        // Optionally rotate the foot bone (ankle→footIndex direction)
        bool isLeft = (leg.Upper == _leftLeg.Upper);
        FootState foot = isLeft ? _leftFoot : _rightFoot;
        if (foot.Bone != null && footIndexVisibility >= visibilityThreshold)
        {
            SolveFoot(ref foot, ankleLm, footIndexLm, blend);
            if (isLeft) _leftFoot = foot; else _rightFoot = foot;
        }
    }

    /// <summary>
    /// Generic limb solver (upper bone + lower bone) using swing-twist with bend plane.
    /// Used by both SolveArm and SolveLeg — the math is the same for any 2-bone limb.
    /// </summary>
    private void SolveLimb(ref ArmState limb, Vector3 upperPos, Vector3 midPos, Vector3 lowerPos, float blend)
    {
        Vector3 upperDir = midPos - upperPos;
        Vector3 lowerDir = lowerPos - midPos;
        if (!Norm(ref upperDir) || !Norm(ref lowerDir)) return;

        // Bend plane from limb triangle
        Vector3 bendLocal = Vector3.Cross(upperDir, lowerDir);
        if (bendLocal.sqrMagnitude < 0.001f)
        {
            bendLocal = limb.LastBendLocal;
        }
        else
        {
            bendLocal.Normalize();
            if (Vector3.Dot(bendLocal, limb.LastBendLocal) < 0f)
                bendLocal = -bendLocal;
        }
        limb.LastBendLocal = bendLocal;

        Vector3 upperWorld = transform.TransformDirection(upperDir);
        Vector3 lowerWorld = transform.TransformDirection(lowerDir);
        Vector3 bendWorld  = transform.TransformDirection(bendLocal);

        if (limb.Upper != null)
        {
            Quaternion target = SwingTwist(limb.UpperBindAim, upperWorld, limb.BindBendNormal, bendWorld, limb.UpperBindRot);
            limb.UpperSmooth = Quaternion.Slerp(limb.UpperSmooth, target, blend);
            limb.Upper.rotation = limb.UpperSmooth;
        }

        if (limb.Lower != null)
        {
            Quaternion target = SwingTwist(limb.LowerBindAim, lowerWorld, limb.BindBendNormal, bendWorld, limb.LowerBindRot);
            limb.LowerSmooth = Quaternion.Slerp(limb.LowerSmooth, target, blend);
            limb.Lower.rotation = limb.LowerSmooth;
        }
    }

    /// <summary>
    /// Foot bone swing from ankle→footIndex direction.
    /// Swing-only (no bend plane) since the foot hinge is a single rotation.
    /// Clamped to footMaxAngle to prevent large noisy rotations in 2D data.
    /// </summary>
    private void SolveFoot(ref FootState foot, Vector3 ankleLm, Vector3 footIndexLm, float blend)
    {
        Vector3 footDir = Lm(footIndexLm) - Lm(ankleLm);
        if (!Norm(ref footDir)) return;

        Vector3 footDirWorld = transform.TransformDirection(footDir);
        Quaternion swing = Quaternion.FromToRotation(foot.BindAimWorld, footDirWorld);

        float angle = Quaternion.Angle(Quaternion.identity, swing);
        if (angle > footMaxAngle)
        {
            swing = Quaternion.Slerp(Quaternion.identity, swing, footMaxAngle / angle);
        }

        Quaternion target = swing * foot.BindRot;
        foot.Smooth = Quaternion.Slerp(foot.Smooth, target, blend * 0.7f); // slightly slower for stability
        foot.Bone.rotation = foot.Smooth;
    }

    /// <summary>Build a FootState from ankle and toes bones.</summary>
    private FootState BuildFoot(HumanBodyBones footBone, HumanBodyBones toesBone)
    {
        Transform f = _animator.GetBoneTransform(footBone);
        Transform t = _animator.GetBoneTransform(toesBone);
        if (f == null) return default;
        if (t == null && f.childCount > 0) t = f.GetChild(0);
        if (t == null) return default;

        Vector3 aim = (t.position - f.position).normalized;
        return new FootState
        {
            Bone = f,
            BindRot = f.rotation,
            BindAimWorld = aim,
            Smooth = f.rotation
        };
    }

    /// <summary>Build a single-swing bone state from a bone and its child (e.g. wrist → middle finger).</summary>
    private FootState BuildSingleSwingBone(HumanBodyBones bone, HumanBodyBones endBone)
    {
        Transform b = _animator.GetBoneTransform(bone);
        Transform e = _animator.GetBoneTransform(endBone);
        if (b == null) return default;
        if (e == null && b.childCount > 0) e = b.GetChild(0);
        if (e == null) return default;

        Vector3 aim = (e.position - b.position).normalized;
        return new FootState
        {
            Bone = b,
            BindRot = b.rotation,
            BindAimWorld = aim,
            Smooth = b.rotation
        };
    }

    /// <summary>Build torso state by finding the best available torso bone (UpperChest → Chest → Spine).</summary>
    private TorsoState BuildTorso()
    {
        // Unity objects use special null semantics — explicit check instead of ?? chain.
        Transform bone = _animator.GetBoneTransform(HumanBodyBones.UpperChest);
        if (bone == null) bone = _animator.GetBoneTransform(HumanBodyBones.Chest);
        if (bone == null) bone = _animator.GetBoneTransform(HumanBodyBones.Spine);
        if (bone == null) return default;

        Transform hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        Vector3 up = hips != null
            ? (bone.position - hips.position).normalized
            : Vector3.up;

        return new TorsoState
        {
            Bone = bone,
            BindRot = bone.rotation,
            BindUpWorld = up,
            Smooth = bone.rotation
        };
    }

    // ═══════════════════════════════════════════
    //  TORSO + HAND SOLVERS
    // ═══════════════════════════════════════════

    /// <summary>
    /// Rotates the spine/chest based on the user's torso "up" direction
    /// (mid-shoulders − mid-hips). Clamped swing, world rotation so the
    /// independently-rotated head isn't dragged along.
    /// </summary>
    private void SolveTorso(float blend)
    {
        if (_torso.Bone == null) return;

        Vector3 midShoulder = 0.5f * (LmRaw(poseManager.LeftShoulder) + LmRaw(poseManager.RightShoulder));
        Vector3 midHip      = 0.5f * (LmRaw(poseManager.LeftHip)      + LmRaw(poseManager.RightHip));
        Vector3 torsoUp = midShoulder - midHip;
        if (!Norm(ref torsoUp)) return;

        Vector3 torsoUpWorld = transform.TransformDirection(torsoUp);
        Quaternion swing = Quaternion.FromToRotation(_torso.BindUpWorld, torsoUpWorld);

        float angle = Quaternion.Angle(Quaternion.identity, swing);
        if (angle > torsoMaxAngle)
            swing = Quaternion.Slerp(Quaternion.identity, swing, torsoMaxAngle / angle);

        Quaternion target = swing * _torso.BindRot;
        _torso.Smooth = Quaternion.Slerp(_torso.Smooth, target, blend);
        _torso.Bone.rotation = _torso.Smooth;
    }

    /// <summary>
    /// Rotates a hand bone based on wrist→index direction (clamped swing).
    /// Mirrored landmarks used (same as arms) so hand direction matches the arm.
    /// </summary>
    private void SolveHand(ref FootState hand, Vector3 wristLm, Vector3 indexLm, float blend)
    {
        if (hand.Bone == null) return;

        Vector3 handDir = Lm(indexLm) - Lm(wristLm);
        if (!Norm(ref handDir)) return;

        Vector3 handDirWorld = transform.TransformDirection(handDir);
        Quaternion swing = Quaternion.FromToRotation(hand.BindAimWorld, handDirWorld);

        float angle = Quaternion.Angle(Quaternion.identity, swing);
        if (angle > handMaxAngle)
            swing = Quaternion.Slerp(Quaternion.identity, swing, handMaxAngle / angle);

        Quaternion target = swing * hand.BindRot;
        hand.Smooth = Quaternion.Slerp(hand.Smooth, target, blend * 0.7f); // slightly slower for stability
        hand.Bone.rotation = hand.Smooth;
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
        // Use raw landmarks (no mirror) for head — head should tilt
        // the SAME direction as the user, not mirrored like arms.
        Vector3 midShoulder = 0.5f * (LmRaw(poseManager.LeftShoulder) + LmRaw(poseManager.RightShoulder));
        Vector3 nosePos = LmRaw(poseManager.Nose);
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
    /// Converts a MediaPipe landmark to root-local 3D position.
    /// x/y from normalized screen coords, z from MediaPipe relative depth.
    /// MediaPipe z: more negative = closer to camera. We map to +Z = forward (toward camera).
    /// </summary>
    private Vector3 Lm(Vector3 landmark)
    {
        float x = landmark.x - 0.5f;
        if (mirrorX) x = -x;
        float y = 0.5f - landmark.y;
        float z = -landmark.z * depthScale; // negate: MP closer=negative, we want closer=positive
        return new Vector3(x, y, z);
    }

    /// <summary>
    /// Same as Lm but without mirror — used for head tilt which should
    /// match the user's direction, not be mirrored like arms.
    /// </summary>
    private Vector3 LmRaw(Vector3 landmark)
    {
        float z = -landmark.z * depthScale;
        return new Vector3(landmark.x - 0.5f, 0.5f - landmark.y, z);
    }

    private static bool Norm(ref Vector3 v)
    {
        float m = v.magnitude;
        if (m < 0.0001f) return false;
        v /= m;
        return true;
    }
}
