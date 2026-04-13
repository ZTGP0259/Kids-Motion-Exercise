using UnityEngine;

/// <summary>
/// Drives a Humanoid character's arm bones using MediaPipe landmark data
/// from PoseDetectionManager. Attach to the character root GameObject.
///
/// SETUP:
///   1. Character must have a Humanoid Avatar configured.
///   2. Drag the PoseDetectionManager's GameObject into the poseManager slot.
///   3. The script fetches bone transforms via Animator at runtime.
/// </summary>
[RequireComponent(typeof(Animator))]
public class PoseToCharacter : MonoBehaviour
{
    [Header("Pose source")]
    public PoseDetectionManager poseManager;

    [Header("Smoothing (higher = snappier)")]
    [Range(1f, 30f)]
    public float smoothSpeed = 10f;

    [Header("World-space reference: character hip centre")]
    [Tooltip("Assign the hip/pelvis bone or an empty at hip height so MediaPipe coords map correctly.")]
    public Transform hipCenter;

    [Header("Scale: how many Unity units = full screen width")]
    public float worldScale = 2.0f;

    // Cached bone transforms
    private Transform _leftUpperArm;
    private Transform _leftLowerArm;
    private Transform _rightUpperArm;
    private Transform _rightLowerArm;

    // Current (smoothed) rotations
    private Quaternion _lUpperRot;
    private Quaternion _lLowerRot;
    private Quaternion _rUpperRot;
    private Quaternion _rLowerRot;

    private Animator _animator;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void Start()
    {
        if (_animator.avatar == null || !_animator.avatar.isHuman)
        {
            Debug.LogError("[PoseToCharacter] Animator must have a Humanoid Avatar assigned.");
            enabled = false;
            return;
        }

        _leftUpperArm  = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _leftLowerArm  = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        _rightUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        _rightLowerArm = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);

        if (_leftUpperArm == null || _rightUpperArm == null)
        {
            Debug.LogError("[PoseToCharacter] Could not find arm bones. Check Humanoid Avatar mapping.");
            enabled = false;
            return;
        }

        // Initialise smoothed rotations to current bone pose
        _lUpperRot = _leftUpperArm.rotation;
        _lLowerRot = _leftLowerArm.rotation;
        _rUpperRot = _rightUpperArm.rotation;
        _rLowerRot = _rightLowerArm.rotation;

        if (hipCenter == null)
            hipCenter = _animator.GetBoneTransform(HumanBodyBones.Hips);
    }

    private void LateUpdate()
    {
        if (poseManager == null) return;

        // ── Convert MediaPipe normalized coords → Unity world space ──
        // Mirror X: MediaPipe image x=0 is left of frame, character's left is +x in world
        Vector3 lShoulderW  = LandmarkToWorld(poseManager.LeftShoulder);
        Vector3 lElbowW     = LandmarkToWorld(poseManager.LeftElbow);
        Vector3 lWristW     = LandmarkToWorld(poseManager.LeftWrist);

        Vector3 rShoulderW  = LandmarkToWorld(poseManager.RightShoulder);
        Vector3 rElbowW     = LandmarkToWorld(poseManager.RightElbow);
        Vector3 rWristW     = LandmarkToWorld(poseManager.RightWrist);

        // ── Skip if visibility is too low (z channel holds visibility score) ──
        bool lVisible = poseManager.LeftShoulder.z  > 0.4f &&
                        poseManager.LeftElbow.z      > 0.4f &&
                        poseManager.LeftWrist.z      > 0.4f;

        bool rVisible = poseManager.RightShoulder.z > 0.4f &&
                        poseManager.RightElbow.z     > 0.4f &&
                        poseManager.RightWrist.z     > 0.4f;

        float t = Time.deltaTime * smoothSpeed;

        // ── LEFT ARM ──
        if (lVisible && _leftUpperArm != null)
        {
            Vector3 upperDir = lElbowW - lShoulderW;
            if (upperDir.sqrMagnitude > 0.0001f)
            {
                Quaternion targetUpper = Quaternion.LookRotation(upperDir, Vector3.up);
                _lUpperRot = Quaternion.Slerp(_lUpperRot, targetUpper, t);
                _leftUpperArm.rotation = _lUpperRot;
            }

            Vector3 lowerDir = lWristW - lElbowW;
            if (lowerDir.sqrMagnitude > 0.0001f && _leftLowerArm != null)
            {
                Quaternion targetLower = Quaternion.LookRotation(lowerDir, Vector3.up);
                _lLowerRot = Quaternion.Slerp(_lLowerRot, targetLower, t);
                _leftLowerArm.rotation = _lLowerRot;
            }
        }

        // ── RIGHT ARM ──
        if (rVisible && _rightUpperArm != null)
        {
            Vector3 upperDir = rElbowW - rShoulderW;
            if (upperDir.sqrMagnitude > 0.0001f)
            {
                Quaternion targetUpper = Quaternion.LookRotation(upperDir, Vector3.up);
                _rUpperRot = Quaternion.Slerp(_rUpperRot, targetUpper, t);
                _rightUpperArm.rotation = _rUpperRot;
            }

            Vector3 lowerDir = rWristW - rElbowW;
            if (lowerDir.sqrMagnitude > 0.0001f && _rightLowerArm != null)
            {
                Quaternion targetLower = Quaternion.LookRotation(lowerDir, Vector3.up);
                _rLowerRot = Quaternion.Slerp(_rLowerRot, targetLower, t);
                _rightLowerArm.rotation = _rLowerRot;
            }
        }
    }

    /// <summary>
    /// Converts a MediaPipe normalized landmark (x,y in 0-1, z = visibility)
    /// to Unity world space, centred on hipCenter.
    /// </summary>
    private Vector3 LandmarkToWorld(Vector3 landmark)
    {
        // Mirror X so character matches viewer (like a mirror)
        float mx = 1f - landmark.x;

        // Map [0,1] → [-0.5, 0.5] then scale
        float wx = (mx - 0.5f) * worldScale;
        float wy = (0.5f - landmark.y) * worldScale; // y flipped: image y=0 is top

        Vector3 origin = hipCenter != null ? hipCenter.position : transform.position;
        return origin + new Vector3(wx, wy, 0f);
    }
}
