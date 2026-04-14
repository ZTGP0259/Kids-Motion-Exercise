using UnityEngine;

/// <summary>
/// Drives a Humanoid character's arm bones to mirror the player's pose
/// detected by MediaPipe via PoseDetectionManager.
///
/// ─── KEY CONCEPT: BEND PLANE ───
///
///   A bone rotation has 3 degrees of freedom (DOF):
///     1. Aim direction (where the bone points)  — 2 DOF
///     2. Twist/roll around the bone axis         — 1 DOF
///
///   FromToRotation only handles the aim (2 DOF). The missing twist
///   determines WHICH WAY the elbow bends — without it, elbows bend
///   in wrong or random directions.
///
///   Fix: use the shoulder→elbow→wrist TRIANGLE to compute a bend plane
///   normal via cross product. This constrains the twist so the elbow
///   bends anatomically.
///
///   Steps per frame:
///     1. Compute arm direction (shoulder→elbow) and forearm direction (elbow→wrist)
///     2. Cross product of these two = bend plane normal (which way elbow faces)
///     3. LookRotation(aimDir, bendNormal) gives a full 3-DOF rotation
///     4. Compute delta from T-pose LookRotation to current LookRotation
///     5. Apply delta to bind-pose bone rotation
///
/// ─── SETUP ───
///   1. Character must have a Humanoid Avatar configured.
///   2. Drag PoseDetectionManager into the poseManager slot.
///   3. Character should face the camera (rotated 180° on Y).
/// </summary>
[RequireComponent(typeof(Animator))]
public class PoseToCharacter : MonoBehaviour
{
    [Header("Pose source")]
    public PoseDetectionManager poseManager;

    [Header("Smoothing")]
    [Range(1f, 30f)]
    [Tooltip("Higher = snappier response, lower = smoother but laggier")]
    public float smoothSpeed = 12f;

    [Header("Visibility")]
    [Range(0.1f, 0.9f)]
    [Tooltip("Minimum MediaPipe visibility score to accept a landmark")]
    public float visibilityThreshold = 0.5f;

    // ── Bone transforms ──
    private Transform _leftUpperArm;
    private Transform _leftLowerArm;
    private Transform _rightUpperArm;
    private Transform _rightLowerArm;

    // ── Bind-pose (T-pose) world rotations ──
    private Quaternion _bindLUpper;
    private Quaternion _bindLLower;
    private Quaternion _bindRUpper;
    private Quaternion _bindRLower;

    // ── Smoothed current rotations ──
    private Quaternion _smoothLUpper;
    private Quaternion _smoothLLower;
    private Quaternion _smoothRUpper;
    private Quaternion _smoothRLower;

    private Animator _animator;
    private int _debugFrameCount;

    // ── T-pose reference directions (MediaPipe 2D, Y-flipped) ──
    // Left arm in T-pose points right in image:  (+1, 0, 0)
    // Right arm in T-pose points left in image:  (-1, 0, 0)
    private static readonly Vector3 kTPoseLeftDir  = Vector3.right;
    private static readonly Vector3 kTPoseRightDir = Vector3.left;

    // ── Default bend normals when arm is straight (cross product = 0) ──
    // Elbows bend toward the camera. Character faces -Z (rotated 180° Y).
    // Left arm cross product gives -Z when bending forward.
    // Right arm cross product gives +Z when bending forward (mirror).
    private static readonly Vector3 kLeftBendNormal  = Vector3.back;    // (0, 0, -1)
    private static readonly Vector3 kRightBendNormal = Vector3.forward; // (0, 0, +1)

    // ── Calibrated T-pose directions (from CalibrationManager) ──
    private Vector3 _calLeftDir;
    private Vector3 _calRightDir;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void Start()
    {
        if (_animator.avatar == null || !_animator.avatar.isHuman)
        {
            Debug.LogError("[PoseToCharacter] Animator must have a Humanoid Avatar.");
            enabled = false;
            return;
        }

        _leftUpperArm  = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _leftLowerArm  = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        _rightUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        _rightLowerArm = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);

        if (_leftUpperArm == null || _rightUpperArm == null)
        {
            Debug.LogError("[PoseToCharacter] Missing arm bones. Check Humanoid Avatar.");
            enabled = false;
            return;
        }

        // Capture bind-pose rotations (character must be in T-pose)
        _bindLUpper = _leftUpperArm.rotation;
        _bindLLower = _leftLowerArm != null ? _leftLowerArm.rotation : Quaternion.identity;
        _bindRUpper = _rightUpperArm.rotation;
        _bindRLower = _rightLowerArm != null ? _rightLowerArm.rotation : Quaternion.identity;

        _smoothLUpper = _bindLUpper;
        _smoothLLower = _bindLLower;
        _smoothRUpper = _bindRUpper;
        _smoothRLower = _bindRLower;

        // Load calibrated T-pose directions or use defaults
        if (CalibrationManager.HasCalibrationData)
        {
            _calLeftDir  = CalibrationManager.CalLeftUpperDir;
            _calRightDir = CalibrationManager.CalRightUpperDir;
            Debug.Log($"[PoseToCharacter] Calibrated: L={_calLeftDir:F2} R={_calRightDir:F2}");
        }
        else
        {
            _calLeftDir  = kTPoseLeftDir;
            _calRightDir = kTPoseRightDir;
            Debug.Log("[PoseToCharacter] Using default T-pose directions.");
        }
    }

    private void LateUpdate()
    {
        if (poseManager == null) return;

        bool lVis = poseManager.LeftShoulder.z  > visibilityThreshold &&
                    poseManager.LeftElbow.z      > visibilityThreshold &&
                    poseManager.LeftWrist.z      > visibilityThreshold;

        bool rVis = poseManager.RightShoulder.z > visibilityThreshold &&
                    poseManager.RightElbow.z     > visibilityThreshold &&
                    poseManager.RightWrist.z     > visibilityThreshold;

        float t = Time.deltaTime * smoothSpeed;

        // Debug logging
        _debugFrameCount++;
        if (_debugFrameCount % 90 == 0)
        {
            Vector3 ls = poseManager.LeftShoulder;
            Vector3 le = poseManager.LeftElbow;
            Vector3 lw = poseManager.LeftWrist;
            Vector3 dir = LandmarkDir(ls, le);
            Vector3 lower = LandmarkDir(le, lw);
            Vector3 bend = Vector3.Cross(dir, lower);
            Debug.Log($"[PoseToChar] lVis={lVis} rVis={rVis} | " +
                      $"upperDir={dir:F2} lowerDir={lower:F2} bendN={bend:F2}");
        }

        if (lVis)
        {
            ApplyArm(
                poseManager.LeftShoulder, poseManager.LeftElbow, poseManager.LeftWrist,
                _leftUpperArm, _leftLowerArm,
                _calLeftDir, kLeftBendNormal,
                _bindLUpper, _bindLLower,
                ref _smoothLUpper, ref _smoothLLower, t);
        }

        if (rVis)
        {
            ApplyArm(
                poseManager.RightShoulder, poseManager.RightElbow, poseManager.RightWrist,
                _rightUpperArm, _rightLowerArm,
                _calRightDir, kRightBendNormal,
                _bindRUpper, _bindRLower,
                ref _smoothRUpper, ref _smoothRLower, t);
        }
    }

    /// <summary>
    /// Computes and applies rotation for one full arm (upper + lower bone).
    ///
    /// The bend plane normal is computed from the cross product of the upper
    /// and lower arm direction vectors. This creates a proper hinge constraint
    /// so the elbow bends in the anatomically correct direction.
    /// </summary>
    private static void ApplyArm(
        Vector3 shoulderLM, Vector3 elbowLM, Vector3 wristLM,
        Transform upperBone, Transform lowerBone,
        Vector3 tposeDir, Vector3 defaultBendNormal,
        Quaternion bindUpper, Quaternion bindLower,
        ref Quaternion smoothUpper, ref Quaternion smoothLower,
        float t)
    {
        Vector3 upperDir = LandmarkDir(shoulderLM, elbowLM);
        Vector3 lowerDir = LandmarkDir(elbowLM, wristLM);

        if (upperDir.sqrMagnitude < 0.001f) return;

        // ── Compute bend plane normal from arm triangle ──
        // cross(shoulder→elbow, elbow→wrist) gives the normal to the plane
        // containing the arm. This determines which way the elbow faces.
        Vector3 bendNormal = Vector3.Cross(upperDir, lowerDir);
        if (bendNormal.sqrMagnitude < 0.0001f)
        {
            // Arms nearly straight (collinear) — no bend plane detectable.
            // Fall back to default: elbows bend toward camera.
            bendNormal = defaultBendNormal;
        }
        else
        {
            bendNormal.Normalize();
        }

        // ── T-pose reference rotation ──
        // In T-pose: arm points along tposeDir, bend plane uses default normal.
        // This is the "zero rotation" reference.
        Quaternion tposeRef = Quaternion.LookRotation(tposeDir, defaultBendNormal);

        // ── Upper arm rotation ──
        if (upperBone != null)
        {
            // Current orientation: aim along upperDir, constrained by bend plane
            Quaternion currentRot = Quaternion.LookRotation(upperDir, bendNormal);

            // Delta = how much the orientation changed from T-pose
            // This captures BOTH the direction change AND the twist change
            Quaternion delta = currentRot * Quaternion.Inverse(tposeRef);

            // Apply delta on top of bind-pose rotation
            Quaternion target = delta * bindUpper;
            smoothUpper = Quaternion.Slerp(smoothUpper, target, t);
            upperBone.rotation = smoothUpper;
        }

        // ── Lower arm rotation ──
        if (lowerBone != null && lowerDir.sqrMagnitude > 0.001f)
        {
            // Lower arm aims along elbow→wrist, same bend plane
            Quaternion currentRot = Quaternion.LookRotation(lowerDir, bendNormal);

            // In T-pose, lower arm has same direction and plane as upper arm
            // (arm is perfectly straight), so we reuse tposeRef
            Quaternion delta = currentRot * Quaternion.Inverse(tposeRef);

            Quaternion target = delta * bindLower;
            smoothLower = Quaternion.Slerp(smoothLower, target, t);
            lowerBone.rotation = smoothLower;
        }
    }

    /// <summary>
    /// Computes a normalized direction from one landmark to another in 2D.
    /// X: direct from MediaPipe (already mirrored by front camera).
    /// Y: flipped (MediaPipe y=0 is top, Unity y+ is up).
    /// Z: always 0 (2D projection).
    /// </summary>
    private static Vector3 LandmarkDir(Vector3 from, Vector3 to)
    {
        float dx = to.x - from.x;
        float dy = -(to.y - from.y);
        return new Vector3(dx, dy, 0f).normalized;
    }
}
