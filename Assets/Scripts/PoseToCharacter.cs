using UnityEngine;

/// <summary>
/// Maps MediaPipe pose landmarks onto a humanoid avatar's arm chain.
///
/// Key idea:
/// - Aim comes from the limb direction.
/// - Twist comes from the bend plane normal computed by the shoulder-elbow-wrist triangle.
/// - Rotations are solved in parent local space so the humanoid hierarchy stays intact.
/// </summary>
[RequireComponent(typeof(Animator))]
public class PoseToCharacter : MonoBehaviour
{
    [Header("Pose Source")]
    public PoseDetectionManager poseManager;

    [Header("Coordinate Conversion")]
    [Tooltip("Enable this when using a front camera preview that behaves like a mirror.")]
    public bool mirrorHorizontally = true;

    [Tooltip("Scale of the hip-centered tracking space. Directions are unaffected, but keeping this non-zero helps debugging.")]
    public float trackingScale = 2f;

    [Tooltip("Scales MediaPipe relative z depth into Unity tracking space.")]
    public float depthScale = 2.5f;

    [Header("Tracking Quality")]
    [Range(0.1f, 0.95f)]
    public float visibilityThreshold = 0.5f;

    [Tooltip("If the arm triangle becomes too flat, reuse the last valid bend plane instead of trusting noisy data.")]
    [Range(0.0001f, 0.05f)]
    public float straightArmEpsilon = 0.0025f;

    [Header("Smoothing")]
    [Tooltip("Higher values reduce lag. Lower values smooth more aggressively.")]
    [Range(1f, 30f)]
    public float rotationSmoothing = 14f;

    [Tooltip("Torso rotation smoothing. Slightly lower values keep the body steady while arms stay responsive.")]
    [Range(1f, 30f)]
    public float torsoSmoothing = 10f;

    [Header("Safety Limits")]
    [Tooltip("Maximum local-space angular distance from the bind pose.")]
    [Range(45f, 180f)]
    public float upperArmMaxDegreesFromBind = 150f;

    [Tooltip("Maximum local-space angular distance from the bind pose.")]
    [Range(45f, 180f)]
    public float lowerArmMaxDegreesFromBind = 165f;

    [Tooltip("Maximum local-space angular distance from the torso bind pose.")]
    [Range(20f, 90f)]
    public float torsoMaxDegreesFromBind = 55f;

    private Animator _animator;
    private Vector3 _bindTorsoForwardWorld;
    private TorsoRuntime _torso;

    private ArmRuntime _leftArm;
    private ArmRuntime _rightArm;

    private struct TorsoRuntime
    {
        public Transform Bone;
        public Quaternion BindLocalRotation;
        public Quaternion SmoothedLocalRotation;
        public Quaternion BindBasisLocal;
    }

    private struct ArmRuntime
    {
        public Transform UpperBone;
        public Transform LowerBone;

        public Quaternion UpperBindLocalRotation;
        public Quaternion LowerBindLocalRotation;

        public Quaternion UpperSmoothedLocalRotation;
        public Quaternion LowerSmoothedLocalRotation;

        public Quaternion UpperBindBasisLocal;
        public Quaternion LowerBindBasisLocal;

        public Vector3 LastPlaneNormalRoot;
        public bool HasLastPlaneNormal;
    }

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void Start()
    {
        if (_animator.avatar == null || !_animator.avatar.isHuman)
        {
            Debug.LogError("[PoseToCharacter] Animator must use a Humanoid avatar.");
            enabled = false;
            return;
        }

        Transform leftUpperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform leftLowerArm = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        Transform leftHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);

        Transform rightUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform rightLowerArm = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform rightHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);

        if (leftUpperArm == null || leftLowerArm == null || rightUpperArm == null || rightLowerArm == null)
        {
            Debug.LogError("[PoseToCharacter] Missing humanoid arm bones.");
            enabled = false;
            return;
        }

        leftHand = ResolveEndBone(leftLowerArm, leftHand);
        rightHand = ResolveEndBone(rightLowerArm, rightHand);

        if (leftHand == null || rightHand == null)
        {
            Debug.LogError("[PoseToCharacter] Missing hand bones or lower-arm children needed for calibration.");
            enabled = false;
            return;
        }

        _bindTorsoForwardWorld = ComputeBindTorsoForwardWorld(leftUpperArm, rightUpperArm);
        _torso = BuildTorsoRuntime();

        _leftArm = BuildArmRuntime(leftUpperArm, leftLowerArm, leftHand);
        _rightArm = BuildArmRuntime(rightUpperArm, rightLowerArm, rightHand);
    }

    private void LateUpdate()
    {
        if (poseManager == null || !enabled)
        {
            return;
        }

        float armBlend = 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);
        float torsoBlend = 1f - Mathf.Exp(-torsoSmoothing * Time.deltaTime);

        Vector3 hipCenter = 0.5f * (poseManager.LeftHip + poseManager.RightHip);

        Vector3 leftShoulder = ToTrackingSpace(poseManager.LeftShoulder, hipCenter);
        Vector3 rightShoulder = ToTrackingSpace(poseManager.RightShoulder, hipCenter);
        Vector3 leftElbow = ToTrackingSpace(poseManager.LeftElbow, hipCenter);
        Vector3 rightElbow = ToTrackingSpace(poseManager.RightElbow, hipCenter);
        Vector3 leftWrist = ToTrackingSpace(poseManager.LeftWrist, hipCenter);
        Vector3 rightWrist = ToTrackingSpace(poseManager.RightWrist, hipCenter);
        Vector3 leftHip = ToTrackingSpace(poseManager.LeftHip, hipCenter);
        Vector3 rightHip = ToTrackingSpace(poseManager.RightHip, hipCenter);

        Vector3 torsoPlaneRoot = ComputeTorsoPlaneRoot(leftShoulder, rightShoulder, leftHip, rightHip);
        if (Vector3.Dot(transform.TransformDirection(torsoPlaneRoot), _bindTorsoForwardWorld) < 0f)
        {
            torsoPlaneRoot = -torsoPlaneRoot;
        }

        Vector3 torsoRightRoot = rightShoulder - leftShoulder;
        Vector3 torsoUpRoot = 0.5f * (leftShoulder + rightShoulder) - 0.5f * (leftHip + rightHip);
        bool torsoVisible = HasTorsoVisibility(
            poseManager.LeftShoulderVisibility,
            poseManager.RightShoulderVisibility,
            poseManager.LeftHipVisibility,
            poseManager.RightHipVisibility);

        if (torsoVisible && TryNormalize(ref torsoRightRoot) && TryNormalize(ref torsoUpRoot))
        {
            // Torso alignment uses shoulders + hips to orient the upper body,
            // so the arms inherit body turn instead of moving against a fixed chest.
            ApplyTorsoRotation(torsoUpRoot, torsoPlaneRoot, torsoBlend);
        }

        bool leftVisible = HasArmVisibility(
            poseManager.LeftShoulderVisibility,
            poseManager.LeftElbowVisibility,
            poseManager.LeftWristVisibility);

        bool rightVisible = HasArmVisibility(
            poseManager.RightShoulderVisibility,
            poseManager.RightElbowVisibility,
            poseManager.RightWristVisibility);

        if (leftVisible)
        {
            SolveArm(
                ref _leftArm,
                leftShoulder,
                leftElbow,
                leftWrist,
                torsoPlaneRoot,
                armBlend,
                upperArmMaxDegreesFromBind,
                lowerArmMaxDegreesFromBind);
        }

        if (rightVisible)
        {
            SolveArm(
                ref _rightArm,
                rightShoulder,
                rightElbow,
                rightWrist,
                torsoPlaneRoot,
                armBlend,
                upperArmMaxDegreesFromBind,
                lowerArmMaxDegreesFromBind);
        }
    }

    private TorsoRuntime BuildTorsoRuntime()
    {
        Transform torsoBone =
            _animator.GetBoneTransform(HumanBodyBones.UpperChest) ??
            _animator.GetBoneTransform(HumanBodyBones.Chest) ??
            _animator.GetBoneTransform(HumanBodyBones.Spine);

        if (torsoBone == null)
        {
            return default;
        }

        Transform leftUpperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform hips = _animator.GetBoneTransform(HumanBodyBones.Hips);

        Vector3 torsoRightWorld = (rightUpperArm.position - leftUpperArm.position).normalized;
        Vector3 torsoUpWorld = torsoBone.position - hips.position;
        if (!TryNormalize(ref torsoUpWorld))
        {
            torsoUpWorld = torsoBone.up;
        }

        Vector3 torsoForwardWorld = Vector3.Cross(torsoRightWorld, torsoUpWorld);
        if (!TryNormalize(ref torsoForwardWorld))
        {
            torsoForwardWorld = _bindTorsoForwardWorld;
        }

        return new TorsoRuntime
        {
            Bone = torsoBone,
            BindLocalRotation = torsoBone.localRotation,
            SmoothedLocalRotation = torsoBone.localRotation,
            BindBasisLocal = BuildBasisFromAxesInParentSpace(torsoBone.parent, torsoForwardWorld, torsoUpWorld)
        };
    }

    private ArmRuntime BuildArmRuntime(Transform upperBone, Transform lowerBone, Transform endBone)
    {
        ArmRuntime arm = new ArmRuntime
        {
            UpperBone = upperBone,
            LowerBone = lowerBone,
            UpperBindLocalRotation = upperBone.localRotation,
            LowerBindLocalRotation = lowerBone.localRotation,
            UpperSmoothedLocalRotation = upperBone.localRotation,
            LowerSmoothedLocalRotation = lowerBone.localRotation
        };

        Vector3 upperAimWorld = (lowerBone.position - upperBone.position).normalized;
        Vector3 lowerAimWorld = (endBone.position - lowerBone.position).normalized;

        Vector3 bindPlaneWorld = Vector3.Cross(upperAimWorld, lowerAimWorld);
        if (bindPlaneWorld.sqrMagnitude < 0.0001f)
        {
            bindPlaneWorld = _bindTorsoForwardWorld;
        }
        bindPlaneWorld.Normalize();

        arm.LastPlaneNormalRoot = transform.InverseTransformDirection(bindPlaneWorld).normalized;
        arm.HasLastPlaneNormal = true;

        arm.UpperBindBasisLocal = BuildBasisInParentSpace(upperBone.parent, upperAimWorld, bindPlaneWorld);
        arm.LowerBindBasisLocal = BuildBasisInParentSpace(lowerBone.parent, lowerAimWorld, bindPlaneWorld);

        return arm;
    }

    private void SolveArm(
        ref ArmRuntime arm,
        Vector3 shoulderRoot,
        Vector3 elbowRoot,
        Vector3 wristRoot,
        Vector3 torsoPlaneRoot,
        float blend,
        float upperClampDegrees,
        float lowerClampDegrees)
    {
        Vector3 upperAimRoot = (elbowRoot - shoulderRoot);
        Vector3 lowerAimRoot = (wristRoot - elbowRoot);

        if (!TryNormalize(ref upperAimRoot) || !TryNormalize(ref lowerAimRoot))
        {
            return;
        }

        Vector3 planeNormalRoot = Vector3.Cross(upperAimRoot, lowerAimRoot);
        if (planeNormalRoot.sqrMagnitude < straightArmEpsilon)
        {
            planeNormalRoot = arm.HasLastPlaneNormal ? arm.LastPlaneNormalRoot : torsoPlaneRoot;
        }
        else
        {
            planeNormalRoot.Normalize();
            if (arm.HasLastPlaneNormal && Vector3.Dot(planeNormalRoot, arm.LastPlaneNormalRoot) < 0f)
            {
                planeNormalRoot = -planeNormalRoot;
            }
        }

        if (Vector3.Dot(planeNormalRoot, torsoPlaneRoot) < -0.15f)
        {
            planeNormalRoot = Vector3.Slerp(planeNormalRoot, torsoPlaneRoot, 0.5f).normalized;
        }

        arm.LastPlaneNormalRoot = planeNormalRoot;
        arm.HasLastPlaneNormal = true;

        Quaternion upperTargetLocal = SolveLocalRotation(
            arm.UpperBone,
            upperAimRoot,
            planeNormalRoot,
            arm.UpperBindBasisLocal,
            arm.UpperBindLocalRotation);

        Quaternion lowerTargetLocal = SolveLocalRotation(
            arm.LowerBone,
            lowerAimRoot,
            planeNormalRoot,
            arm.LowerBindBasisLocal,
            arm.LowerBindLocalRotation);

        upperTargetLocal = ClampFromBind(arm.UpperBindLocalRotation, upperTargetLocal, upperClampDegrees);
        lowerTargetLocal = ClampFromBind(arm.LowerBindLocalRotation, lowerTargetLocal, lowerClampDegrees);

        arm.UpperSmoothedLocalRotation = Quaternion.Slerp(arm.UpperSmoothedLocalRotation, upperTargetLocal, blend);
        arm.LowerSmoothedLocalRotation = Quaternion.Slerp(arm.LowerSmoothedLocalRotation, lowerTargetLocal, blend);

        arm.UpperBone.localRotation = arm.UpperSmoothedLocalRotation;
        arm.LowerBone.localRotation = arm.LowerSmoothedLocalRotation;
    }

    private void ApplyTorsoRotation(Vector3 torsoUpRoot, Vector3 torsoForwardRoot, float blend)
    {
        if (_torso.Bone == null)
        {
            return;
        }

        Vector3 torsoForwardWorld = transform.TransformDirection(torsoForwardRoot);
        Vector3 torsoUpWorld = transform.TransformDirection(torsoUpRoot);

        Quaternion targetBasisLocal = BuildBasisFromAxesInParentSpace(
            _torso.Bone.parent,
            torsoForwardWorld,
            torsoUpWorld);

        Quaternion deltaLocal = targetBasisLocal * Quaternion.Inverse(_torso.BindBasisLocal);
        Quaternion targetLocalRotation = deltaLocal * _torso.BindLocalRotation;
        targetLocalRotation = ClampFromBind(_torso.BindLocalRotation, targetLocalRotation, torsoMaxDegreesFromBind);

        _torso.SmoothedLocalRotation = Quaternion.Slerp(_torso.SmoothedLocalRotation, targetLocalRotation, blend);
        _torso.Bone.localRotation = _torso.SmoothedLocalRotation;
    }

    private Quaternion SolveLocalRotation(
        Transform bone,
        Vector3 aimRoot,
        Vector3 planeNormalRoot,
        Quaternion bindBasisLocal,
        Quaternion bindLocalRotation)
    {
        Vector3 aimWorld = transform.TransformDirection(aimRoot);
        Vector3 planeWorld = transform.TransformDirection(planeNormalRoot);
        Quaternion targetBasisLocal = BuildBasisInParentSpace(bone.parent, aimWorld, planeWorld);
        Quaternion deltaLocal = targetBasisLocal * Quaternion.Inverse(bindBasisLocal);
        return deltaLocal * bindLocalRotation;
    }

    private static Quaternion ClampFromBind(Quaternion bindRotation, Quaternion targetRotation, float maxDegrees)
    {
        float angle = Quaternion.Angle(bindRotation, targetRotation);
        if (angle <= maxDegrees)
        {
            return targetRotation;
        }

        float t = maxDegrees / angle;
        return Quaternion.Slerp(bindRotation, targetRotation, t);
    }

    private static Quaternion BuildBasisInParentSpace(Transform parent, Vector3 aimWorld, Vector3 planeNormalWorld)
    {
        Vector3 aimParent = parent.InverseTransformDirection(aimWorld).normalized;
        Vector3 planeParent = parent.InverseTransformDirection(planeNormalWorld).normalized;

        Vector3 upParent = Vector3.Cross(planeParent, aimParent);
        if (!TryNormalize(ref upParent))
        {
            upParent = FindPerpendicular(aimParent);
        }

        return Quaternion.LookRotation(aimParent, upParent);
    }

    private static Quaternion BuildBasisFromAxesInParentSpace(Transform parent, Vector3 forwardWorld, Vector3 upWorld)
    {
        Vector3 forwardParent = parent.InverseTransformDirection(forwardWorld).normalized;
        Vector3 upParent = parent.InverseTransformDirection(upWorld).normalized;

        if (!TryNormalize(ref forwardParent))
        {
            forwardParent = Vector3.forward;
        }

        if (!TryNormalize(ref upParent))
        {
            upParent = FindPerpendicular(forwardParent);
        }

        Vector3 rightParent = Vector3.Cross(upParent, forwardParent);
        if (!TryNormalize(ref rightParent))
        {
            rightParent = FindPerpendicular(forwardParent);
        }

        upParent = Vector3.Cross(forwardParent, rightParent).normalized;
        return Quaternion.LookRotation(forwardParent, upParent);
    }

    private Vector3 ToTrackingSpace(Vector3 landmark, Vector3 hipCenter)
    {
        // MediaPipe is image-space:
        // - x grows right
        // - y grows down
        // - z is relative depth (smaller / more negative is closer to the camera)
        // We convert into a hip-centered Unity tracking space where:
        // - +x is character right
        // - +y is up
        // - +z is toward the camera / forward reach
        float x = landmark.x - hipCenter.x;
        if (mirrorHorizontally)
        {
            x = -x;
        }

        float y = hipCenter.y - landmark.y;
        float z = (hipCenter.z - landmark.z) * depthScale;

        return new Vector3(x * trackingScale, y * trackingScale, z);
    }

    private Vector3 ComputeTorsoPlaneRoot(Vector3 leftShoulder, Vector3 rightShoulder, Vector3 leftHip, Vector3 rightHip)
    {
        Vector3 torsoRight = rightShoulder - leftShoulder;
        Vector3 torsoUp = 0.5f * (leftShoulder + rightShoulder) - 0.5f * (leftHip + rightHip);

        if (!TryNormalize(ref torsoRight) || !TryNormalize(ref torsoUp))
        {
            return transform.InverseTransformDirection(_bindTorsoForwardWorld).normalized;
        }

        Vector3 torsoPlane = Vector3.Cross(torsoRight, torsoUp);
        if (!TryNormalize(ref torsoPlane))
        {
            torsoPlane = transform.InverseTransformDirection(_bindTorsoForwardWorld).normalized;
        }

        return torsoPlane;
    }

    private bool HasArmVisibility(float shoulderVisibility, float elbowVisibility, float wristVisibility)
    {
        return shoulderVisibility >= visibilityThreshold &&
               elbowVisibility >= visibilityThreshold &&
               wristVisibility >= visibilityThreshold;
    }

    private bool HasTorsoVisibility(float leftShoulderVisibility, float rightShoulderVisibility, float leftHipVisibility, float rightHipVisibility)
    {
        return leftShoulderVisibility >= visibilityThreshold &&
               rightShoulderVisibility >= visibilityThreshold &&
               leftHipVisibility >= visibilityThreshold &&
               rightHipVisibility >= visibilityThreshold;
    }

    private static Transform ResolveEndBone(Transform lowerBone, Transform handBone)
    {
        if (handBone != null)
        {
            return handBone;
        }

        return lowerBone.childCount > 0 ? lowerBone.GetChild(0) : null;
    }

    private Vector3 ComputeBindTorsoForwardWorld(Transform leftUpperArm, Transform rightUpperArm)
    {
        Transform hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform spine = _animator.GetBoneTransform(HumanBodyBones.Spine);

        Vector3 torsoRight = (rightUpperArm.position - leftUpperArm.position).normalized;
        Vector3 torsoUp = spine != null && hips != null
            ? (spine.position - hips.position).normalized
            : transform.up;

        Vector3 forward = Vector3.Cross(torsoRight, torsoUp);
        if (!TryNormalize(ref forward))
        {
            forward = transform.forward;
        }

        return forward;
    }

    private static bool TryNormalize(ref Vector3 v)
    {
        float magnitude = v.magnitude;
        if (magnitude < 0.0001f)
        {
            return false;
        }

        v /= magnitude;
        return true;
    }

    private static Vector3 FindPerpendicular(Vector3 direction)
    {
        Vector3 axis = Mathf.Abs(direction.y) < 0.95f ? Vector3.up : Vector3.right;
        Vector3 perpendicular = Vector3.Cross(axis, direction);
        return perpendicular.sqrMagnitude > 0.0001f ? perpendicular.normalized : Vector3.forward;
    }
}
